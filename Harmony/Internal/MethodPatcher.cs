using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace HarmonyLib
{
	internal class MethodPatcher
	{
		/// special parameter names that can be used in prefix and postfix methods

		public const string INSTANCE_PARAM = "__instance";
		public const string ORIGINAL_METHOD_PARAM = "__originalMethod";
		public const string RESULT_VAR = "__result";
		public const string STATE_VAR = "__state";
		public const string EXCEPTION_VAR = "__exception";
		public const string PARAM_INDEX_PREFIX = "__";
		public const string INSTANCE_FIELD_PREFIX = "___";

		readonly bool debug;
		readonly MethodBase original;
		readonly MethodBase source;
		readonly List<MethodInfo> prefixes;
		readonly List<MethodInfo> postfixes;
		readonly List<MethodInfo> transpilers;
		readonly List<MethodInfo> finalizers;
		readonly int idx;
		readonly bool firstArgIsReturnBuffer;
		readonly Type returnType;
		readonly DynamicMethod patch;
		readonly ILGenerator il;
		readonly Emitter emitter;

		internal MethodPatcher(MethodBase original, MethodBase source, List<MethodInfo> prefixes, List<MethodInfo> postfixes, List<MethodInfo> transpilers, List<MethodInfo> finalizers, bool debug)
		{
			if (original == null)
				throw new ArgumentNullException(nameof(original));

			this.debug = debug;
			this.original = original;
			this.source = source;
			this.prefixes = prefixes;
			this.postfixes = postfixes;
			this.transpilers = transpilers;
			this.finalizers = finalizers;

			Memory.MarkForNoInlining(original);

			if (debug)
			{
				FileLog.LogBuffered($"### Patch {original.FullDescription()}");
				FileLog.FlushBuffer();
			}

			idx = prefixes.Count() + postfixes.Count() + finalizers.Count();
			firstArgIsReturnBuffer = NativeThisPointer.NeedsNativeThisPointerFix(original);
			if (debug && firstArgIsReturnBuffer) FileLog.Log($"### Special case: extra argument after 'this' is pointer to valuetype (simulate return value)");
			returnType = AccessTools.GetReturnedType(original);
			patch = CreateDynamicMethod(original, $"_Patch{idx}", debug);
			if (patch == null)
				throw new Exception("Could not create replacement method");

			il = patch.GetILGenerator();
			emitter = new Emitter(il, debug);
		}

		internal DynamicMethod CreateReplacement(out Dictionary<int, CodeInstruction> finalInstructions)
		{
			var originalVariables = DeclareLocalVariables(source ?? original);
			var privateVars = new Dictionary<string, LocalBuilder>();

			LocalBuilder resultVariable = null;
			if (idx > 0)
			{
				resultVariable = DeclareLocalVariable(returnType);
				privateVars[RESULT_VAR] = resultVariable;
			}

			prefixes.Union(postfixes).Union(finalizers).ToList().ForEach(fix =>
			{
				if (fix.DeclaringType != null && privateVars.ContainsKey(fix.DeclaringType.FullName) == false)
				{
					fix.GetParameters()
					.Where(patchParam => patchParam.Name == STATE_VAR)
					.Do(patchParam =>
					{
						var privateStateVariable = DeclareLocalVariable(patchParam.ParameterType);
						privateVars[fix.DeclaringType.FullName] = privateStateVariable;
					});
				}
			});

			LocalBuilder finalizedVariable = null;
			if (finalizers.Any())
			{
				finalizedVariable = DeclareLocalVariable(typeof(bool));

				privateVars[EXCEPTION_VAR] = DeclareLocalVariable(typeof(Exception));

				// begin try
				emitter.MarkBlockBefore(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock), out _);
			}

			if (firstArgIsReturnBuffer)
				emitter.Emit(original.IsStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1); // load ref to return value

			var skipOriginalLabel = il.DefineLabel();
			var canHaveJump = AddPrefixes(privateVars, skipOriginalLabel);

			var copier = new MethodCopier(source ?? original, il, originalVariables);
			foreach (var transpiler in transpilers)
				copier.AddTranspiler(transpiler);
			if (firstArgIsReturnBuffer)
			{
				if (original.IsStatic)
					copier.AddTranspiler(NativeThisPointer.m_ArgumentShiftTranspilerStatic);
				else
					copier.AddTranspiler(NativeThisPointer.m_ArgumentShiftTranspilerInstance);
			}

			var endLabels = new List<Label>();
			copier.Finalize(emitter, endLabels, out var endingReturn);

			foreach (var label in endLabels)
				emitter.MarkLabel(label);
			if (resultVariable != null)
				emitter.Emit(OpCodes.Stloc, resultVariable);
			if (canHaveJump)
				emitter.MarkLabel(skipOriginalLabel);

			AddPostfixes(privateVars, false);

			if (resultVariable != null)
				emitter.Emit(OpCodes.Ldloc, resultVariable);

			AddPostfixes(privateVars, true);

			var hasFinalizers = finalizers.Any();
			if (hasFinalizers)
			{
				_ = AddFinalizers(privateVars, false);
				emitter.Emit(OpCodes.Ldc_I4_1);
				emitter.Emit(OpCodes.Stloc, finalizedVariable);
				var noExceptionLabel1 = il.DefineLabel();
				emitter.Emit(OpCodes.Ldloc, privateVars[EXCEPTION_VAR]);
				emitter.Emit(OpCodes.Brfalse, noExceptionLabel1);
				emitter.Emit(OpCodes.Ldloc, privateVars[EXCEPTION_VAR]);
				emitter.Emit(OpCodes.Throw);
				emitter.MarkLabel(noExceptionLabel1);

				// end try, begin catch
				emitter.MarkBlockBefore(new ExceptionBlock(ExceptionBlockType.BeginCatchBlock), out var label);
				emitter.Emit(OpCodes.Stloc, privateVars[EXCEPTION_VAR]);

				emitter.Emit(OpCodes.Ldloc, finalizedVariable);
				var endFinalizerLabel = il.DefineLabel();
				emitter.Emit(OpCodes.Brtrue, endFinalizerLabel);

				var rethrowPossible = AddFinalizers(privateVars, true);

				emitter.MarkLabel(endFinalizerLabel);

				var noExceptionLabel2 = il.DefineLabel();
				emitter.Emit(OpCodes.Ldloc, privateVars[EXCEPTION_VAR]);
				emitter.Emit(OpCodes.Brfalse, noExceptionLabel2);
				if (rethrowPossible)
					emitter.Emit(OpCodes.Rethrow);
				else
				{
					emitter.Emit(OpCodes.Ldloc, privateVars[EXCEPTION_VAR]);
					emitter.Emit(OpCodes.Throw);
				}
				emitter.MarkLabel(noExceptionLabel2);

				// end catch
				emitter.MarkBlockAfter(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));

				if (resultVariable != null)
					emitter.Emit(OpCodes.Ldloc, resultVariable);
			}

			if (firstArgIsReturnBuffer)
				emitter.Emit(OpCodes.Stobj, returnType); // store result into ref

			if (hasFinalizers || endingReturn)
				emitter.Emit(OpCodes.Ret);

			finalInstructions = emitter.GetInstructions();

			if (debug)
			{
				FileLog.LogBuffered("DONE");
				FileLog.LogBuffered("");
				FileLog.FlushBuffer();
			}

			PrepareDynamicMethod(patch);
			return patch;
		}

		internal static DynamicMethod CreateDynamicMethod(MethodBase original, string suffix, bool debug)
		{
			if (original == null) throw new ArgumentNullException(nameof(original));
			var patchName = original.Name + suffix;
			patchName = patchName.Replace("<>", "");

			var parameters = original.GetParameters();
			var parameterTypes = parameters.Types().ToList();

			var firstArgIsReturnBuffer = NativeThisPointer.NeedsNativeThisPointerFix(original);
			if (firstArgIsReturnBuffer)
				parameterTypes.Insert(0, typeof(IntPtr));

			if (original.IsStatic == false)
			{
				if (AccessTools.IsStruct(original.DeclaringType))
					parameterTypes.Insert(0, original.DeclaringType.MakeByRefType());
				else
					parameterTypes.Insert(0, original.DeclaringType);
			}

			var returnType = firstArgIsReturnBuffer ? typeof(void) : AccessTools.GetReturnedType(original);

			// DynamicMethod does not support byref return types
			if (returnType == null || returnType.IsByRef)
				return null;

			if (debug || Harmony.DEBUG)
			{
				var args = parameterTypes.Select(type => type.FullDescription()).ToArray().Join();
				FileLog.Log($">>> {original.DeclaringType.FullDescription()} static {returnType.FullDescription()} {patchName}({args})");
			}

			var method = new DynamicMethod(
				patchName,
				MethodAttributes.Public | MethodAttributes.Static,
				CallingConventions.Standard,
				returnType,
				parameterTypes.ToArray(),
				original.DeclaringType,
				true
			);

#if NETSTANDARD2_0 || NETCOREAPP2_0
#else
			var offset = (original.IsStatic ? 0 : 1) + (firstArgIsReturnBuffer ? 1 : 0);
			/*if (firstArgIsReturnBuffer)
				method.Definition.Parameters[0].Name = "retbuf";
			if (!original.IsStatic)
				method.Definition.Parameters[firstArgIsReturnBuffer ? 1 : 0].Name = "this";*/
			for (var i = 0; i < parameters.Length; i++)
				_ = method.DefineParameter(i + offset, parameters[i].Attributes, parameters[i].Name);
#endif

			return method;
		}

		internal static void PrepareDynamicMethod(DynamicMethod method)
		{
			var nonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;
			var nonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;

			// on mono, just call 'CreateDynMethod'	
			//	
			var m_CreateDynMethod = method.GetType().GetMethod("CreateDynMethod", nonPublicInstance);
			if (m_CreateDynMethod != null)
			{
				var h_CreateDynMethod = MethodInvoker.GetHandler(m_CreateDynMethod);
				_ = h_CreateDynMethod(method, new object[0]);
				return;
			}

			// on all .NET Core versions, call 'RuntimeHelpers._CompileMethod' but with a different parameter:	
			//	
			var m__CompileMethod = typeof(RuntimeHelpers).GetMethod("_CompileMethod", nonPublicStatic);
			var h__CompileMethod = MethodInvoker.GetHandler(m__CompileMethod);

			var m_GetMethodDescriptor = method.GetType().GetMethod("GetMethodDescriptor", nonPublicInstance);
			var h_GetMethodDescriptor = MethodInvoker.GetHandler(m_GetMethodDescriptor);
			var handle = (RuntimeMethodHandle)h_GetMethodDescriptor(method, new object[0]);

			// 1) RuntimeHelpers._CompileMethod(handle.GetMethodInfo())	
			//	
			object runtimeMethodInfo = null;
			var f_m_value = handle.GetType().GetField("m_value", nonPublicInstance);
			if (f_m_value != null)
				runtimeMethodInfo = f_m_value.GetValue(handle);
			else
			{
				var f_Value = handle.GetType().GetField("Value", nonPublicInstance);
				if (f_Value != null)
					runtimeMethodInfo = f_Value.GetValue(handle);
				else
				{
					var m_GetMethodInfo = handle.GetType().GetMethod("GetMethodInfo", nonPublicInstance);
					if (m_GetMethodInfo != null)
					{
						var h_GetMethodInfo = MethodInvoker.GetHandler(m_GetMethodInfo);
						runtimeMethodInfo = h_GetMethodInfo(handle, new object[0]);
					}
				}
			}
			if (runtimeMethodInfo != null)
			{
				// Core 3.1 fails for certain methods in the try statement below	
				// The following extraction is wrong and stands here as a reminder that	
				// we need to fix that runtimeMethodInfo sometimes is a RuntimeMethodInfoStub	
				//	
				//f_m_value = runtimeMethodInfo.GetType().GetField("m_value");	
				//if (f_m_value != null)	
				//	runtimeMethodInfo = f_m_value.GetValue(runtimeMethodInfo);	

				try
				{
					// this can throw BadImageFormatException "An attempt was made to load a program with an incorrect format"	
					_ = h__CompileMethod(null, new object[] { runtimeMethodInfo });
					return;
				}
#pragma warning disable RECS0022
				catch
#pragma warning restore RECS0022
				{
				}
			}

			// 2) RuntimeHelpers._CompileMethod(handle.Value)	
			//	
			if (m__CompileMethod.GetParameters()[0].ParameterType.IsAssignableFrom(handle.Value.GetType()))
			{
				_ = h__CompileMethod(null, new object[] { handle.Value });
				return;
			}

			// 3) RuntimeHelpers._CompileMethod(handle)	
			//	
			if (m__CompileMethod.GetParameters()[0].ParameterType.IsAssignableFrom(handle.GetType()))
			{
				_ = h__CompileMethod(null, new object[] { handle });
				return;
			}
		}

		LocalBuilder[] DeclareLocalVariables(MethodBase member)
		{
			var vars = member.GetMethodBody()?.LocalVariables;
			if (vars == null)
				return new LocalBuilder[0];
			return vars.Select(lvi => il.DeclareLocal(lvi.LocalType, lvi.IsPinned)).ToArray();
		}

		LocalBuilder DeclareLocalVariable(Type type)
		{
			if (type.IsByRef) type = type.GetElementType();
			if (type.IsEnum) type = Enum.GetUnderlyingType(type);

			if (AccessTools.IsClass(type))
			{
				var v = il.DeclareLocal(type);
				emitter.Emit(OpCodes.Ldnull);
				emitter.Emit(OpCodes.Stloc, v);
				return v;
			}
			if (AccessTools.IsStruct(type))
			{
				var v = il.DeclareLocal(type);
				emitter.Emit(OpCodes.Ldloca, v);
				emitter.Emit(OpCodes.Initobj, type);
				return v;
			}
			if (AccessTools.IsValue(type))
			{
				var v = il.DeclareLocal(type);
				if (type == typeof(float))
					emitter.Emit(OpCodes.Ldc_R4, (float)0);
				else if (type == typeof(double))
					emitter.Emit(OpCodes.Ldc_R8, (double)0);
				else if (type == typeof(long) || type == typeof(ulong))
					emitter.Emit(OpCodes.Ldc_I8, (long)0);
				else
					emitter.Emit(OpCodes.Ldc_I4, 0);
				emitter.Emit(OpCodes.Stloc, v);
				return v;
			}
			return null;
		}

		static OpCode LoadIndOpCodeFor(Type type)
		{
			if (type.IsEnum)
				return OpCodes.Ldind_I4;

			if (type == typeof(float)) return OpCodes.Ldind_R4;
			if (type == typeof(double)) return OpCodes.Ldind_R8;

			if (type == typeof(byte)) return OpCodes.Ldind_U1;
			if (type == typeof(ushort)) return OpCodes.Ldind_U2;
			if (type == typeof(uint)) return OpCodes.Ldind_U4;
			if (type == typeof(ulong)) return OpCodes.Ldind_I8;

			if (type == typeof(sbyte)) return OpCodes.Ldind_I1;
			if (type == typeof(short)) return OpCodes.Ldind_I2;
			if (type == typeof(int)) return OpCodes.Ldind_I4;
			if (type == typeof(long)) return OpCodes.Ldind_I8;

			return OpCodes.Ldind_Ref;
		}

		static readonly MethodInfo m_GetMethodFromHandle1 = typeof(MethodBase).GetMethod("GetMethodFromHandle", new[] { typeof(RuntimeMethodHandle) });
		static readonly MethodInfo m_GetMethodFromHandle2 = typeof(MethodBase).GetMethod("GetMethodFromHandle", new[] { typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle) });
		bool EmitOriginalBaseMethod()
		{
			if (original is MethodInfo method)
				emitter.Emit(OpCodes.Ldtoken, method);
			else if (original is ConstructorInfo constructor)
				emitter.Emit(OpCodes.Ldtoken, constructor);
			else return false;

			var type = original.ReflectedType;
			if (type.IsGenericType) emitter.Emit(OpCodes.Ldtoken, type);
			emitter.Emit(OpCodes.Call, type.IsGenericType ? m_GetMethodFromHandle2 : m_GetMethodFromHandle1);
			return true;
		}

		void EmitCallParameter(MethodInfo patch, Dictionary<string, LocalBuilder> variables, bool allowFirsParamPassthrough)
		{
			var isInstance = original.IsStatic == false;
			var originalParameters = original.GetParameters();
			var originalParameterNames = originalParameters.Select(p => p.Name).ToArray();

			// check for passthrough using first parameter (which must have same type as return type)
			var parameters = patch.GetParameters().ToList();
			if (allowFirsParamPassthrough && patch.ReturnType != typeof(void) && parameters.Count > 0 && parameters[0].ParameterType == patch.ReturnType)
				parameters.RemoveRange(0, 1);

			foreach (var patchParam in parameters)
			{
				if (patchParam.Name == ORIGINAL_METHOD_PARAM)
				{
					if (EmitOriginalBaseMethod())
						continue;

					emitter.Emit(OpCodes.Ldnull);
					continue;
				}

				if (patchParam.Name == INSTANCE_PARAM)
				{
					if (original.IsStatic)
						emitter.Emit(OpCodes.Ldnull);
					else
					{
						var instanceIsRef = original.DeclaringType != null && AccessTools.IsStruct(original.DeclaringType);
						var parameterIsRef = patchParam.ParameterType.IsByRef;
						if (instanceIsRef == parameterIsRef)
						{
							emitter.Emit(OpCodes.Ldarg_0);
						}
						if (instanceIsRef && parameterIsRef == false)
						{
							emitter.Emit(OpCodes.Ldarg_0);
							emitter.Emit(OpCodes.Ldobj, original.DeclaringType);
						}
						if (instanceIsRef == false && parameterIsRef)
						{
							emitter.Emit(OpCodes.Ldarga, 0);
						}
					}
					continue;
				}

				if (patchParam.Name.StartsWith(INSTANCE_FIELD_PREFIX, StringComparison.Ordinal))
				{
					var fieldName = patchParam.Name.Substring(INSTANCE_FIELD_PREFIX.Length);
					FieldInfo fieldInfo;
					if (fieldName.All(char.IsDigit))
					{
						// field access by index only works for declared fields
						fieldInfo = AccessTools.DeclaredField(original.DeclaringType, int.Parse(fieldName));
						if (fieldInfo == null)
							throw new ArgumentException($"No field found at given index in class {original.DeclaringType.FullName}", fieldName);
					}
					else
					{
						fieldInfo = AccessTools.Field(original.DeclaringType, fieldName);
						if (fieldInfo == null)
							throw new ArgumentException($"No such field defined in class {original.DeclaringType.FullName}", fieldName);
					}

					if (fieldInfo.IsStatic)
						emitter.Emit(patchParam.ParameterType.IsByRef ? OpCodes.Ldsflda : OpCodes.Ldsfld, fieldInfo);
					else
					{
						emitter.Emit(OpCodes.Ldarg_0);
						emitter.Emit(patchParam.ParameterType.IsByRef ? OpCodes.Ldflda : OpCodes.Ldfld, fieldInfo);
					}
					continue;
				}

				// state is special too since each patch has its own local var
				if (patchParam.Name == STATE_VAR)
				{
					var ldlocCode = patchParam.ParameterType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc;
					if (variables.TryGetValue(patch.DeclaringType.FullName, out var stateVar))
						emitter.Emit(ldlocCode, stateVar);
					else
						emitter.Emit(OpCodes.Ldnull);
					continue;
				}

				// treat __result var special
				if (patchParam.Name == RESULT_VAR)
				{
					var returnType = AccessTools.GetReturnedType(original);
					if (returnType == typeof(void))
						throw new Exception($"Cannot get result from void method {original.FullDescription()}");
					var resultType = patchParam.ParameterType;
					if (resultType.IsByRef)
						resultType = resultType.GetElementType();
					if (resultType.IsAssignableFrom(returnType) == false)
						throw new Exception($"Cannot assign method return type {returnType.FullName} to {RESULT_VAR} type {resultType.FullName} for method {original.FullDescription()}");
					var ldlocCode = patchParam.ParameterType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc;
					emitter.Emit(ldlocCode, variables[RESULT_VAR]);
					continue;
				}

				// any other declared variables
				if (variables.TryGetValue(patchParam.Name, out var localBuilder))
				{
					var ldlocCode = patchParam.ParameterType.IsByRef ? OpCodes.Ldloca : OpCodes.Ldloc;
					emitter.Emit(ldlocCode, localBuilder);
					continue;
				}

				int idx;
				if (patchParam.Name.StartsWith(PARAM_INDEX_PREFIX, StringComparison.Ordinal))
				{
					var val = patchParam.Name.Substring(PARAM_INDEX_PREFIX.Length);
					if (!int.TryParse(val, out idx))
						throw new Exception($"Parameter {patchParam.Name} does not contain a valid index");
					if (idx < 0 || idx >= originalParameters.Length)
						throw new Exception($"No parameter found at index {idx}");
				}
				else
				{
					idx = patch.GetArgumentIndex(originalParameterNames, patchParam);
					if (idx == -1) throw new Exception($"Parameter \"{patchParam.Name}\" not found in method {original.FullDescription()}");
				}

				//   original -> patch     opcode
				// --------------------------------------
				// 1 normal   -> normal  : LDARG
				// 2 normal   -> ref/out : LDARGA
				// 3 ref/out  -> normal  : LDARG, LDIND_x
				// 4 ref/out  -> ref/out : LDARG
				//
				var originalIsNormal = originalParameters[idx].IsOut == false && originalParameters[idx].ParameterType.IsByRef == false;
				var patchIsNormal = patchParam.IsOut == false && patchParam.ParameterType.IsByRef == false;
				var patchArgIndex = idx + (isInstance ? 1 : 0) + (firstArgIsReturnBuffer ? 1 : 0);

				// Case 1 + 4
				if (originalIsNormal == patchIsNormal)
				{
					emitter.Emit(OpCodes.Ldarg, patchArgIndex);
					continue;
				}

				// Case 2
				if (originalIsNormal && patchIsNormal == false)
				{
					emitter.Emit(OpCodes.Ldarga, patchArgIndex);
					continue;
				}

				// Case 3
				emitter.Emit(OpCodes.Ldarg, patchArgIndex);
				emitter.Emit(LoadIndOpCodeFor(originalParameters[idx].ParameterType));
			}
		}

		bool AddPrefixes(Dictionary<string, LocalBuilder> variables, Label label)
		{
			var canHaveJump = false;
			prefixes.ForEach(fix =>
			{
				if (original.GetMethodBody() == null)
					throw new Exception("Methods without body cannot have prefixes. Use a transpiler instead.");

				EmitCallParameter(fix, variables, false);
				emitter.Emit(OpCodes.Call, fix);

				if (fix.ReturnType != typeof(void))
				{
					if (fix.ReturnType != typeof(bool))
						throw new Exception($"Prefix patch {fix} has not \"bool\" or \"void\" return type: {fix.ReturnType}");
					emitter.Emit(OpCodes.Brfalse, label);
					canHaveJump = true;
				}
			});
			return canHaveJump;
		}

		void AddPostfixes(Dictionary<string, LocalBuilder> variables, bool passthroughPatches)
		{
			postfixes
				.Where(fix => passthroughPatches == (fix.ReturnType != typeof(void)))
				.Do(fix =>
				{
					if (original.GetMethodBody() == null)
						throw new Exception("Methods without body cannot have postfixes. Use a transpiler instead.");

					EmitCallParameter(fix, variables, true);
					emitter.Emit(OpCodes.Call, fix);

					if (fix.ReturnType != typeof(void))
					{
						var firstFixParam = fix.GetParameters().FirstOrDefault();
						var hasPassThroughResultParam = firstFixParam != null && fix.ReturnType == firstFixParam.ParameterType;
						if (!hasPassThroughResultParam)
						{
							if (firstFixParam != null)
								throw new Exception($"Return type of pass through postfix {fix} does not match type of its first parameter");

							throw new Exception($"Postfix patch {fix} must have a \"void\" return type");
						}
					}
				});
		}

		bool AddFinalizers(Dictionary<string, LocalBuilder> variables, bool catchExceptions)
		{
			var rethrowPossible = true;
			finalizers
				.Do(fix =>
				{
					if (original.GetMethodBody() == null)
						throw new Exception("Methods without body cannot have finalizers. Use a transpiler instead.");

					if (catchExceptions)
						emitter.MarkBlockBefore(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock), out var label);

					EmitCallParameter(fix, variables, false);
					emitter.Emit(OpCodes.Call, fix);
					if (fix.ReturnType != typeof(void))
					{
						emitter.Emit(OpCodes.Stloc, variables[EXCEPTION_VAR]);
						rethrowPossible = false;
					}

					if (catchExceptions)
					{
						emitter.MarkBlockBefore(new ExceptionBlock(ExceptionBlockType.BeginCatchBlock), out _);
						emitter.Emit(OpCodes.Pop);
						emitter.MarkBlockAfter(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
					}
				});

			return rethrowPossible;
		}
	}
}
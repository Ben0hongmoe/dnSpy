﻿/*
    Copyright (C) 2014-2018 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.DotNet.Steppers.Engine;
using dnSpy.Contracts.Debugger.Engine.Steppers;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Decompiler;
using dnSpy.Debugger.DotNet.Code;
using dnSpy.Debugger.DotNet.Metadata;
using dnSpy.Debugger.DotNet.Properties;

namespace dnSpy.Debugger.DotNet.Steppers.Engine {
	sealed class DbgEngineStepperImpl : DbgEngineStepper {
		public override event EventHandler<DbgEngineStepCompleteEventArgs> StepComplete;

		readonly DbgLanguageService dbgLanguageService;
		readonly DbgDotNetDebugInfoService dbgDotNetDebugInfoService;
		readonly DebuggerSettings debuggerSettings;
		readonly IDbgDotNetRuntime runtime;
		readonly DbgDotNetEngineStepper stepper;
		ReturnToAwaiterState returnToAwaiterState;
		StepIntoState stepIntoState;

		sealed class ReturnToAwaiterState {
			public DbgDotNetStepperBreakpoint breakpoint;
			public TaskCompletionSource<DbgThread> taskCompletionSource;
			public DbgDotNetObjectId taskObjectId;
		}

		DbgThread CurrentThread {
			get => __DONT_USE_currentThread;
			set => __DONT_USE_currentThread = value ?? throw new InvalidOperationException();
		}
		DbgThread __DONT_USE_currentThread;

		public DbgEngineStepperImpl(DbgLanguageService dbgLanguageService, DbgDotNetDebugInfoService dbgDotNetDebugInfoService, DebuggerSettings debuggerSettings, IDbgDotNetRuntime runtime, DbgDotNetEngineStepper stepper, DbgThread thread) {
			this.dbgLanguageService = dbgLanguageService ?? throw new ArgumentNullException(nameof(dbgLanguageService));
			this.dbgDotNetDebugInfoService = dbgDotNetDebugInfoService ?? throw new ArgumentNullException(nameof(dbgDotNetDebugInfoService));
			this.debuggerSettings = debuggerSettings ?? throw new ArgumentNullException(nameof(debuggerSettings));
			this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
			this.stepper = stepper ?? throw new ArgumentNullException(nameof(stepper));
			CurrentThread = thread ?? throw new ArgumentNullException(nameof(thread));
		}

		DbgEvaluationInfo CreateEvaluationInfo(DbgThread thread) {
			DbgStackFrame frame = null;
			try {
				frame = thread.GetTopStackFrame();
				if (frame == null)
					throw new InvalidOperationException();
				Debug.Assert(frame != null);
				return CreateEvaluationInfo(frame);
			}
			catch {
				frame?.Close();
				throw;
			}
		}

		DbgEvaluationInfo CreateEvaluationInfo(DbgStackFrame frame) {
			DbgEvaluationContext ctx = null;
			try {
				var language = dbgLanguageService.GetCurrentLanguage(frame.Runtime.RuntimeKindGuid);
				ctx = language.CreateContext(frame, DbgEvaluationContextOptions.NoMethodBody);
				var cancellationToken = CancellationToken.None;
				return new DbgEvaluationInfo(ctx, frame, cancellationToken);
			}
			catch {
				ctx?.Close();
				throw;
			}
		}

		void ClearReturnToAwaiterState() {
			ClearReturnToAwaiterBreakpoint();
			ClearReturnToAwaiterTaskObjectId();
		}

		Task<DbgThread> TryCreateReturnToAwaiterTask(DbgThread threadOrNull, DbgModule module, uint methodToken, uint setResultOffset, uint builderFieldToken) {
			runtime.Dispatcher.VerifyAccess();
			ClearReturnToAwaiterState();
			if (!debuggerSettings.AsyncDebugging)
				return null;
			if (setResultOffset == uint.MaxValue)
				return null;
			if (!TaskEvalUtils.SupportsAsyncStepOut(module.GetReflectionModule().AppDomain))
				return null;
			if ((runtime.Features & DbgDotNetRuntimeFeatures.NoAsyncStepObjectId) != 0)
				return null;
			if (returnToAwaiterState == null)
				returnToAwaiterState = new ReturnToAwaiterState();
			returnToAwaiterState.breakpoint = stepper.CreateBreakpoint(threadOrNull, module, methodToken, setResultOffset);
			returnToAwaiterState.taskCompletionSource = new TaskCompletionSource<DbgThread>();
			var tcs = returnToAwaiterState.taskCompletionSource;
			returnToAwaiterState.breakpoint.Hit += (s, e) => {
				if (returnToAwaiterState?.taskCompletionSource == tcs) {
					e.Pause = true;
					tcs.TrySetResult(e.Thread);
				}
				else
					tcs.TrySetCanceled();
			};
			return CreateReturnToAwaiterTaskCoreAsync(returnToAwaiterState.taskCompletionSource.Task, module, builderFieldToken);
		}

		async Task<DbgThread> CreateReturnToAwaiterTaskCoreAsync(Task<DbgThread> setResultBreakpointTask, DbgModule builderFieldModule, uint builderFieldToken) {
			runtime.Dispatcher.VerifyAccess();
			var thread = await setResultBreakpointTask;
			ClearReturnToAwaiterState();
			stepper.CancelLastStep();
			DbgDotNetValue taskValue = null;
			try {
				if (TryCallSetNotificationForWaitCompletion(thread, builderFieldModule, builderFieldToken, true, out taskValue)) {
					var notifyDebuggerOfWaitCompletionMethod = TaskEvalUtils.GetNotifyDebuggerOfWaitCompletionMethod(taskValue.Type.AppDomain);
					Debug.Assert((object)notifyDebuggerOfWaitCompletionMethod != null);
					thread = await SetNotifyDebuggerOfWaitCompletionBreakpoint(notifyDebuggerOfWaitCompletionMethod, taskValue);
				}
			}
			finally {
				taskValue?.Dispose();
			}
			return thread;
		}

		Task<DbgThread> SetNotifyDebuggerOfWaitCompletionBreakpoint(DmdMethodInfo method, DbgDotNetValue taskValue) {
			try {
				runtime.Dispatcher.VerifyAccess();
				ClearReturnToAwaiterState();
				var module = method.Module.GetDebuggerModule() ?? throw new InvalidOperationException();
				if (returnToAwaiterState == null)
					returnToAwaiterState = new ReturnToAwaiterState();
				returnToAwaiterState.breakpoint = stepper.CreateBreakpoint(null, module, (uint)method.MetadataToken, 0);
				returnToAwaiterState.taskCompletionSource = new TaskCompletionSource<DbgThread>();

				if ((runtime.Features & DbgDotNetRuntimeFeatures.ObjectIds) != 0)
					returnToAwaiterState.taskObjectId = runtime.CreateObjectId(taskValue, 0);
				var taskObjId = returnToAwaiterState.taskObjectId;

				var tcs = returnToAwaiterState.taskCompletionSource;
				returnToAwaiterState.breakpoint.Hit += (s, e) => {
					if (tcs != returnToAwaiterState?.taskCompletionSource) {
						tcs.TrySetCanceled();
						return;
					}
					bool hit;
					if (taskObjId == null)
						hit = true;
					else {
						DbgDotNetValueResult taskValue2 = default;
						DbgEvaluationInfo evalInfo = null;
						try {
							evalInfo = CreateEvaluationInfo(e.Thread);
							taskValue2 = runtime.GetParameterValue(evalInfo, 0);
							hit = !taskValue2.IsNormalResult || runtime.Equals(taskObjId, taskValue2.Value);
						}
						finally {
							taskValue2.Value?.Dispose();
							evalInfo?.Close();
						}
					}
					if (hit) {
						e.Pause = true;
						tcs.TrySetResult(e.Thread);
					}
				};
				return SetNotifyDebuggerOfWaitCompletionBreakpointCoreAsync(returnToAwaiterState.taskCompletionSource.Task);
			}
			finally {
				taskValue?.Dispose();
			}
		}

		async Task<DbgThread> SetNotifyDebuggerOfWaitCompletionBreakpointCoreAsync(Task<DbgThread> notifyDebuggerOfWaitCompletionBreakpointTask) {
			runtime.Dispatcher.VerifyAccess();
			stepper.Continue();
			var thread = await notifyDebuggerOfWaitCompletionBreakpointTask;
			ClearReturnToAwaiterState();

			// Step out until we reach user code. We could set a BP too, but it's only supported by the CorDebug code.
			// We don't mark any code as user code so we can't just step once and let the CLR stepper do the work.

			for (int i = 0; ; i++) {
				const int MAX_STEP_OUT = 50;
				Debug.Assert(i < MAX_STEP_OUT);
				if (i >= MAX_STEP_OUT)
					break;

				DbgStackFrame[] frames = null;
				try {
					frames = thread.GetFrames(2);
					if (frames.Length <= 1)
						break;
					if (IsUserFrame(frames[0]))
						break;

					var frame = stepper.TryGetFrameInfo(thread);
					Debug.Assert(frame != null);
					if (frame == null)
						break;
					thread = await stepper.StepOutAsync(frame);
				}
				finally {
					thread.Process.DbgManager.Close(frames);
				}
			}

			// Step over any hidden instructions so we end up on a statement
			var newFrame = stepper.TryGetFrameInfo(thread);
			Debug.Assert(newFrame != null);
			if (newFrame != null)
				thread = await StepOverHiddenInstructionsAsync(newFrame);

			return thread;
		}

		bool IsUserFrame(DbgStackFrame frame) {
			runtime.Dispatcher.VerifyAccess();
			DbgEvaluationInfo evalInfo = null;
			try {
				evalInfo = CreateEvaluationInfo(frame);
				var method = runtime.GetFrameMethod(evalInfo);
				if ((object)method == null)
					return false;

				var type = method.DeclaringType;
				while (type.DeclaringType is DmdType declType)
					type = declType;
				if (IsNonUserCodeNamespace(type.MetadataNamespace))
					return false;

				// Assume it's user code
				return true;
			}
			finally {
				evalInfo?.Close();
			}
		}

		static bool IsNonUserCodeNamespace(string @namespace) {
			foreach (var ns in nonUserCodeNamespaces) {
				if (@namespace == ns)
					return true;
			}
			return false;
		}
		static readonly string[] nonUserCodeNamespaces = new string[] {
			// eg. Task, Task<T>
			"System.Threading.Tasks",
			// eg. TaskAwaiter, TaskAwaiter<T>
			"System.Runtime.CompilerServices",
		};

		void ClearReturnToAwaiterBreakpoint() {
			runtime.Dispatcher.VerifyAccess();
			if (returnToAwaiterState == null)
				return;
			returnToAwaiterState.taskCompletionSource?.TrySetCanceled();
			returnToAwaiterState.taskCompletionSource = null;
			if (returnToAwaiterState.breakpoint != null) {
				stepper.RemoveBreakpoints(new[] { returnToAwaiterState.breakpoint });
				returnToAwaiterState.breakpoint = null;
			}
		}

		void ClearReturnToAwaiterTaskObjectId() {
			runtime.Dispatcher.VerifyAccess();
			if (returnToAwaiterState == null)
				return;
			returnToAwaiterState.taskObjectId?.Dispose();
			returnToAwaiterState.taskObjectId = null;
		}

		bool TryCallSetNotificationForWaitCompletion(DbgThread thread, DbgModule builderFieldModule, uint builderFieldToken, bool value, out DbgDotNetValue taskValue) {
			runtime.Dispatcher.VerifyAccess();
			DbgEvaluationInfo evalInfo = null;
			try {
				evalInfo = CreateEvaluationInfo(thread);
				var info = TaskEvalUtils.CallSetNotificationForWaitCompletion(evalInfo, builderFieldModule, builderFieldToken, value);
				taskValue = info.taskValue;
				return info.success;
			}
			finally {
				evalInfo?.Close();
			}
		}

		void RaiseStepComplete(object tag, string error, bool forciblyCanceled = false) {
			runtime.Dispatcher.VerifyAccess();
			CleanUp();
			if (IsClosed)
				return;
			var thread = CurrentThread.IsClosed ? null : CurrentThread;
			Debug.Assert(StepComplete != null);
			StepComplete?.Invoke(this, new DbgEngineStepCompleteEventArgs(thread, tag, error, forciblyCanceled));
		}

		public override void Step(object tag, DbgEngineStepKind step) => runtime.Dispatcher.BeginInvoke(() => Step_EngineThread(tag, step));
		void Step_EngineThread(object tag, DbgEngineStepKind step) {
			runtime.Dispatcher.VerifyAccess();

			if (stepper.Session != null) {
				Debug.Fail("The previous step hasn't been canceled");
				// No need to localize it, if we're here it's a bug
				RaiseStepComplete(tag, "The previous step hasn't been canceled");
				return;
			}

			if (!stepper.IsRuntimePaused) {
				Debug.Fail("Process is not paused");
				// No need to localize it, if we're here it's a bug
				RaiseStepComplete(tag, "Process is not paused");
				return;
			}

			StepAsync(tag, step).ContinueWith(t => {
				var ex = t.Exception;
				Debug.Assert(ex == null);
			});
		}

		Task StepAsync(object tag, DbgEngineStepKind step) {
			runtime.Dispatcher.VerifyAccess();
			switch (step) {
			case DbgEngineStepKind.StepInto:	return StepIntoAsync(tag);
			case DbgEngineStepKind.StepOver:	return StepOverAsync(tag);
			case DbgEngineStepKind.StepOut:		return StepOutAsync(tag);
			default:
				RaiseStepComplete(tag, $"Unsupported step kind: {step}");
				return Task.CompletedTask;
			}
		}

		async Task<DbgThread> StepOverHiddenInstructionsAsync(DbgDotNetEngineStepperFrameInfo frame) {
			runtime.Dispatcher.VerifyAccess();
			var result = await GetStepRangesAsync(frame, returnValues: false);
			return await StepOverHiddenInstructionsAsync(frame, result);
		}

		Task<DbgThread> StepOverHiddenInstructionsAsync(DbgDotNetEngineStepperFrameInfo frame, GetStepRangesAsyncResult result) {
			var thread = frame.Thread;
			if (result.DebugInfoOrNull != null) {
				if (!frame.TryGetLocation(out var module, out var token, out var offset))
					throw new InvalidOperationException();
				bool skipMethod = offset == 0 && IsIgnoredIteratorStateMachineMethod(result.DebugInfoOrNull.Method);
				if (!skipMethod) {
					var currentStatement = result.DebugInfoOrNull.GetSourceStatementByCodeOffset(offset);
					if (currentStatement == null) {
						var ranges = CreateStepRanges(result.DebugInfoOrNull.GetUnusedRanges());
						if (ranges.Length != 0)
							return stepper.StepOverAsync(frame, ranges);
					}
					return Task.FromResult(thread);
				}
			}

			var ilSpans = TryCreateMethodBodySpans(frame);
			if (ilSpans != null) {
				var ranges = CreateStepRanges(ilSpans);
				if (ranges.Length != 0)
					return stepper.StepOverAsync(frame, ranges);
			}
			return Task.FromResult(thread);
		}

		//TODO: This method should return false if iterator methods aren't decompiled (eg. option is disabled or language doesn't support it, eg. IL)
		static bool IsIgnoredIteratorStateMachineMethod(MethodDef method) {
			var declType = method.DeclaringType;
			if (declType.DeclaringType == null)
				return false;
			if (!declType.IsDefined(utf8System_Runtime_CompilerServices, utf8CompilerGeneratedAttribute))
				return false;

			bool result = false;
			foreach (var ii in declType.Interfaces) {
				if (ii.Interface.FullName == "System.Collections.IEnumerator") {
					result = true;
					break;
				}
			}
			if (!result)
				return false;

			if (method.IsVirtual) {
				// Ignore everything except MoveNext
				if (method.Name == utf8MoveNext)
					return false;
				foreach (var ovr in method.Overrides) {
					if (ovr.MethodDeclaration.Name == utf8MoveNext)
						return false;
				}
			}
			return true;
		}
		static readonly UTF8String utf8MoveNext = new UTF8String("MoveNext");
		static readonly UTF8String utf8System_Runtime_CompilerServices = new UTF8String("System.Runtime.CompilerServices");
		static readonly UTF8String utf8CompilerGeneratedAttribute = new UTF8String("CompilerGeneratedAttribute");

		sealed class MethodILSpanState {
			public ILSpan[] BodyRange;
		}

		ILSpan[] TryCreateMethodBodySpans(DbgDotNetEngineStepperFrameInfo frame) {
			if (!frame.TryGetLocation(out var module, out var token, out var offset))
				return null;
			DbgEvaluationInfo evalInfo = null;
			try {
				evalInfo = CreateEvaluationInfo(frame.Thread);
				var method = runtime.GetFrameMethod(evalInfo);
				if ((object)method == null)
					return null;
				var state = method.GetOrCreateData<MethodILSpanState>();
				if (state.BodyRange == null) {
					var body = method.GetMethodBody();
					state.BodyRange = new ILSpan[] { new ILSpan(0, (uint)body.GetILAsByteArray().Length) };
				}
				return state.BodyRange;
			}
			finally {
				evalInfo?.Close();
			}
		}

		async Task StepIntoAsync(object tag) {
			runtime.Dispatcher.VerifyAccess();
			Debug.Assert(stepper.Session == null);
			try {
				var frame = stepper.TryGetFrameInfo(CurrentThread);
				if (frame == null) {
					// No frame? Just let the process run.
					stepper.Continue();
					return;
				}

				stepper.Session = stepper.CreateSession(tag);
				CurrentThread = await StepIntoCoreAsync(frame);
				StepCompleted(null, tag);
			}
			catch (ForciblyCanceledException fce) {
				StepCompleted(fce.Message, tag);
			}
			catch (StepErrorException see) {
				StepError(see.Message, tag);
			}
			catch (Exception ex) {
				if (stepper.IgnoreException(ex))
					return;
				StepFailed(ex, tag);
			}
		}

		async static Task<Task<T>> WhenAny<T>(IEnumerable<Task<T>> tasks) {
			var list = new List<Task<T>>(tasks);
			Debug.Assert(list.Count != 0);
			for (;;) {
				var task = await Task.WhenAny(list);
				if (task.Status != TaskStatus.Canceled)
					return task;
				list.Remove(task);
				if (list.Count == 0)
					return task;
			}
		}

		sealed class StepIntoState {
			public DbgDotNetStepperBreakpoint breakpoint;
			public TaskCompletionSource<DbgThread> taskCompletionSource;
		}

		void ClearStepIntoState() => ClearStepIntoBreakpoint();

		void ClearStepIntoBreakpoint() {
			runtime.Dispatcher.VerifyAccess();
			if (stepIntoState == null)
				return;
			if (stepIntoState.breakpoint != null) {
				stepper.RemoveBreakpoints(new[] { stepIntoState.breakpoint });
				stepIntoState.breakpoint = null;
			}
			stepIntoState.taskCompletionSource?.TrySetCanceled();
			stepIntoState.taskCompletionSource = null;
		}

		Task<DbgThread> SetStepIntoBreakpoint(DbgThread thread, DbgModule module, uint token, uint offset) {
			runtime.Dispatcher.VerifyAccess();
			ClearStepIntoBreakpoint();
			if (stepIntoState == null)
				stepIntoState = new StepIntoState();
			stepIntoState.taskCompletionSource = new TaskCompletionSource<DbgThread>();
			stepIntoState.breakpoint = stepper.CreateBreakpoint(thread, module, token, offset);
			var tcs = stepIntoState.taskCompletionSource;
			stepIntoState.breakpoint.Hit += (s, e) => {
				if (stepIntoState?.taskCompletionSource != tcs)
					tcs.TrySetCanceled();
				else {
					e.Pause = true;
					tcs.TrySetResult(e.Thread);
				}
			};
			return stepIntoState.taskCompletionSource.Task;
		}

		async Task<DbgThread> StepIntoCoreAsync(DbgDotNetEngineStepperFrameInfo frame) {
			runtime.Dispatcher.VerifyAccess();
			var origResult = await GetStepRangesAsync(frame, returnValues: true);
			if (!frame.TryGetLocation(out var origModule, out var origToken, out _))
				throw new InvalidOperationException();
			DbgThread thread;
			var prevFrame = frame;
			bool inSameFrame;
			stepper.CollectReturnValues(frame, origResult.StatementInstructions);
			for (;;) {
				thread = await StepIntoCoreAsync(frame, origResult.DebugInfoOrNull, origResult.StatementRanges);
				frame = stepper.TryGetFrameInfo(thread);
				Debug.Assert(frame != null);
				if (frame == null)
					return thread;
				if (!frame.TryGetLocation(out var module, out var token, out uint offset))
					throw new InvalidOperationException();

				// If we're at the start of the method we may need to skip the first hidden instructions.
				// If it's an async kickoff method, we need to set a BP in MoveNext() and continue the process.
				if (offset == 0) {
					if (debuggerSettings.AsyncDebugging) {
						var newResult = await GetStepRangesAsync(frame, returnValues: false);
						if (newResult.DebugInfoOrNull != null && newResult.StateMachineDebugInfoOrNull?.AsyncInfo != null) {
							var stepIntoTask = SetStepIntoBreakpoint(thread, module, newResult.StateMachineDebugInfoOrNull.Method.MDToken.Raw, 0);
							stepper.Continue();
							thread = await stepIntoTask;
							ClearStepIntoState();
							frame = stepper.TryGetFrameInfo(thread);
							Debug.Assert(frame != null);
							if (frame == null)
								return thread;
						}
					}
					thread = await StepOverHiddenInstructionsAsync(frame);
					frame = stepper.TryGetFrameInfo(thread);
					Debug.Assert(frame != null);
					if (frame == null)
						return thread;
					if (!frame.TryGetLocation(out module, out token, out offset))
						throw new InvalidOperationException();
				}

				// Check if we didn't step into a new method. frame.Equals() isn't always 100% reliable
				// so we also check the offset. If it's not 0, we didn't step into it.
				inSameFrame = origModule == module && origToken == token && (offset != 0 || prevFrame.Equals(frame));
				if (inSameFrame && !Contains(origResult.StatementRanges, offset))
					break;

				if (!inSameFrame) {
					if (debuggerSettings.StepOverPropertiesAndOperators && IsPropertyOrOperatorMethod(thread, out var member)) {
						thread.Runtime.Process.DbgManager.WriteMessage(PredefinedDbgManagerMessageKinds.StepFilter, GetStepFilterMessage(member));
						thread = await stepper.StepOutAsync(frame);
						frame = stepper.TryGetFrameInfo(thread);
						Debug.Assert(frame != null);
						if (frame == null)
							return thread;
						if (!frame.TryGetLocation(out module, out token, out offset))
							throw new InvalidOperationException();
						if (origModule != module || origToken != token)
							break;
					}
				}

				inSameFrame = origModule == module && origToken == token && (offset != 0 || prevFrame.Equals(frame));
				if (!inSameFrame || !Contains(origResult.StatementRanges, offset))
					break;
			}
			if (!inSameFrame) {
				// Clear return values. These should only be shown if we're still in the same frame.
				stepper.ClearReturnValues();
			}
			return thread;
		}

		string GetStepFilterMessage(DmdMemberInfo member) {
			var type = member.DeclaringType;
			if (type.IsConstructedGenericType)
				type = type.GetGenericTypeDefinition();
			var typeName = type.FullName;
			int index = typeName.IndexOf('`');
			if (index >= 0)
				typeName = typeName.Substring(0, index);
			var memberName = typeName + "." + member.Name;

			switch (member) {
			case DmdPropertyInfo _:
				return string.Format(dnSpy_Debugger_DotNet_Resources.StepFilter_SteppingOverProperty, memberName);

			case DmdMethodBase _:
				return string.Format(dnSpy_Debugger_DotNet_Resources.StepFilter_SteppingOverOperator, memberName);

			default:
				Debug.Fail($"Unknown member: {member}");
				return string.Empty;
			}
		}

		static bool Contains(DbgCodeRange[] ranges, uint offset) {
			foreach (var range in ranges) {
				if (range.Contains(offset))
					return true;
			}
			return false;
		}

		bool IsPropertyOrOperatorMethod(DbgThread thread, out DmdMemberInfo member) {
			DbgEvaluationInfo evalInfo = null;
			try {
				evalInfo = CreateEvaluationInfo(thread);
				var method = runtime.GetFrameMethod(evalInfo);
				if ((object)method != null) {
					// Operators should have special-name bit set
					if (method.IsSpecialName && method.Name.StartsWith("op_")) {
						member = method;
						return true;
					}

					if (GetProperty(method) is DmdPropertyInfo property) {
						member = property;
						return true;
					}
				}

				member = null;
				return false;
			}
			finally {
				evalInfo?.Close();
			}
		}

		static DmdPropertyInfo GetProperty(DmdMethodBase method) {
			foreach (var p in method.DeclaringType.DeclaredProperties) {
				if (p.GetGetMethod(DmdGetAccessorOptions.All) == method)
					return p;
				if (p.GetSetMethod(DmdGetAccessorOptions.All) == method)
					return p;
			}
			return null;
		}

		async Task<DbgThread> StepIntoCoreAsync(DbgDotNetEngineStepperFrameInfo frame, MethodDebugInfo debugInfoOrNull, DbgCodeRange[] statementRanges) {
			if (debugInfoOrNull?.AsyncInfo != null && debugInfoOrNull.AsyncInfo.SetResultOffset != uint.MaxValue) {
				if (!frame.TryGetLocation(out var module, out var token, out _))
					throw new InvalidOperationException();
				var returnToAwaiterTask = TryCreateReturnToAwaiterTask(frame.Thread, module, token, debugInfoOrNull.AsyncInfo.SetResultOffset, debugInfoOrNull.AsyncInfo.BuilderFieldOrNull?.MDToken.Raw ?? 0);
				if (returnToAwaiterTask != null) {
					var stepIntoTask = stepper.StepIntoAsync(frame, statementRanges);
					return await await WhenAny(new[] { returnToAwaiterTask, stepIntoTask });
				}
			}
			return await stepper.StepIntoAsync(frame, statementRanges);
		}

		async Task StepOverAsync(object tag) {
			runtime.Dispatcher.VerifyAccess();
			Debug.Assert(stepper.Session == null);
			try {
				var frame = stepper.TryGetFrameInfo(CurrentThread);
				if (frame == null) {
					// No frame? Just let the process run.
					stepper.Continue();
					return;
				}

				stepper.Session = stepper.CreateSession(tag);
				CurrentThread = await StepOverCoreAsync(frame);
				StepCompleted(null, tag);
			}
			catch (ForciblyCanceledException fce) {
				StepCompleted(fce.Message, tag);
			}
			catch (StepErrorException see) {
				StepError(see.Message, tag);
			}
			catch (Exception ex) {
				if (stepper.IgnoreException(ex))
					return;
				StepFailed(ex, tag);
			}
		}

		async Task<DbgThread> StepOverCoreAsync(DbgDotNetEngineStepperFrameInfo frame) {
			runtime.Dispatcher.VerifyAccess();
			Debug.Assert(stepper.Session != null);

			DbgThread thread;
			var result = await GetStepRangesAsync(frame, returnValues: true);
			if (!result.Frame.TryGetLocation(out var module, out var token, out _))
				throw new InvalidOperationException();
			var returnToAwaiterTask = result.DebugInfoOrNull?.AsyncInfo == null ? null : TryCreateReturnToAwaiterTask(result.Frame.Thread, module, token, result.DebugInfoOrNull.AsyncInfo.SetResultOffset, result.DebugInfoOrNull.AsyncInfo.BuilderFieldOrNull?.MDToken.Raw ?? 0);

			var asyncStepInfos = GetAsyncStepInfos(result);
			Debug.Assert(asyncStepInfos == null || asyncStepInfos.Count != 0);
			if (asyncStepInfos != null) {
				try {
					var asyncState = SetAsyncStepOverState(new AsyncStepOverState(this, stepper, result.DebugInfoOrNull.AsyncInfo.BuilderFieldOrNull));
					foreach (var stepInfo in asyncStepInfos)
						asyncState.AddYieldBreakpoint(result.Frame.Thread, module, token, stepInfo);
					var yieldBreakpointTask = asyncState.Task;

					stepper.CollectReturnValues(result.Frame, result.StatementInstructions);
					var stepOverTask = stepper.StepOverAsync(result.Frame, result.StatementRanges);
					var tasks = returnToAwaiterTask == null ? new[] { stepOverTask, yieldBreakpointTask } : new[] { stepOverTask, yieldBreakpointTask, returnToAwaiterTask };
					var completedTask = await Task.WhenAny(tasks);
					ClearReturnToAwaiterState();
					if (completedTask == stepOverTask) {
						asyncState.Dispose();
						thread = stepOverTask.Result;
					}
					else if (completedTask == returnToAwaiterTask) {
						asyncState.Dispose();
						thread = returnToAwaiterTask.Result;
					}
					else {
						stepper.CancelLastStep();
						asyncState.ClearYieldBreakpoints();
						var resumeBpTask = asyncState.SetResumeBreakpoint(result.Frame.Thread, module);
						stepper.Continue();
						thread = await resumeBpTask;
						asyncState.Dispose();

						var newFrame = stepper.TryGetFrameInfo(thread);
						Debug.Assert(newFrame != null);
						if (newFrame != null && newFrame.TryGetLocation(out var newModule, out var newToken, out var newOffset)) {
							Debug.Assert(newModule == module && asyncState.ResumeToken == newToken);
							if (result.DebugInfoOrNull != null && newModule == module && asyncState.ResumeToken == newToken)
								thread = await StepOverHiddenInstructionsAsync(newFrame, result);
						}
					}
				}
				finally {
					SetAsyncStepOverState(null);
					ClearReturnToAwaiterState();
					stepper.CancelLastStep();
				}
			}
			else {
				var tasks = new List<Task<DbgThread>>(2);
				if (returnToAwaiterTask != null)
					tasks.Add(returnToAwaiterTask);
				stepper.CollectReturnValues(result.Frame, result.StatementInstructions);
				tasks.Add(stepper.StepOverAsync(result.Frame, result.StatementRanges));
				thread = await await WhenAny(tasks);
				ClearReturnToAwaiterState();
			}

			return thread;
		}

		List<AsyncStepInfo> GetAsyncStepInfos(in GetStepRangesAsyncResult result) {
			runtime.Dispatcher.VerifyAccess();
			if (!debuggerSettings.AsyncDebugging)
				return null;
			if (result.DebugInfoOrNull?.AsyncInfo == null)
				return null;
			List<AsyncStepInfo> asyncStepInfos = null;
			GetAsyncStepInfos(ref asyncStepInfos, result.DebugInfoOrNull.Method, result.DebugInfoOrNull.AsyncInfo, result.ExactStatementRanges);
			foreach (var ranges in GetHiddenRanges(result.ExactStatementRanges, result.DebugInfoOrNull.GetUnusedRanges()))
				GetAsyncStepInfos(ref asyncStepInfos, result.DebugInfoOrNull.Method, result.DebugInfoOrNull.AsyncInfo, ranges);
			return asyncStepInfos;
		}

		AsyncStepOverState SetAsyncStepOverState(AsyncStepOverState state) {
			runtime.Dispatcher.VerifyAccess();
			__DONT_USE_asyncStepOverState?.Dispose();
			__DONT_USE_asyncStepOverState = state;
			return state;
		}
		AsyncStepOverState __DONT_USE_asyncStepOverState;

		sealed class AsyncStepOverState {
			readonly DbgEngineStepperImpl owner;
			readonly DbgDotNetEngineStepper stepper;
			readonly List<AsyncBreakpointState> yieldBreakpoints;
			readonly TaskCompletionSource<AsyncBreakpointState> yieldTaskCompletionSource;
			DbgDotNetStepperBreakpoint resumeBreakpoint;

			public Task Task => yieldTaskCompletionSource.Task;
			public uint ResumeToken { get; private set; }
			DbgModule builderFieldModule;
			readonly uint builderFieldToken;
			DbgDotNetObjectId taskObjectId;

			public AsyncStepOverState(DbgEngineStepperImpl owner, DbgDotNetEngineStepper stepper, FieldDef builderFieldOrNull) {
				this.owner = owner;
				this.stepper = stepper;
				yieldBreakpoints = new List<AsyncBreakpointState>();
				yieldTaskCompletionSource = new TaskCompletionSource<AsyncBreakpointState>();
				builderFieldToken = builderFieldOrNull?.MDToken.Raw ?? 0;
			}

			public void AddYieldBreakpoint(DbgThread thread, DbgModule module, uint token, AsyncStepInfo stepInfo) {
				var yieldBreakpoint = stepper.CreateBreakpoint(thread, module, token, stepInfo.YieldOffset);
				try {
					var bpState = new AsyncBreakpointState(yieldBreakpoint, stepInfo.ResumeMethod, stepInfo.ResumeOffset);
					bpState.Hit += AsyncBreakpointState_Hit;
					yieldBreakpoints.Add(bpState);
				}
				catch {
					stepper.RemoveBreakpoints(new[] { yieldBreakpoint });
					throw;
				}
			}

			void AsyncBreakpointState_Hit(object sender, AsyncBreakpointState bpState) => yieldTaskCompletionSource.TrySetResult(bpState);

			internal Task<DbgThread> SetResumeBreakpoint(DbgThread thread, DbgModule module) {
				Debug.Assert(yieldTaskCompletionSource.Task.IsCompleted);
				Debug.Assert(resumeBreakpoint == null);
				if (resumeBreakpoint != null)
					throw new InvalidOperationException();
				var bpState = yieldTaskCompletionSource.Task.GetAwaiter().GetResult();
				builderFieldModule = module;
				ResumeToken = bpState.ResumeMethod.MDToken.Raw;

				DbgDotNetValue taskObjId = null;
				try {
					var runtime = module.Runtime.GetDotNetRuntime();
					if ((runtime.Features & DbgDotNetRuntimeFeatures.ObjectIds) != 0 && (runtime.Features & DbgDotNetRuntimeFeatures.NoAsyncStepObjectId) == 0) {
						taskObjId = TryGetTaskObjectId(thread);
						if (taskObjId != null)
							taskObjectId = runtime.CreateObjectId(taskObjId, 0);
					}

					// The thread can change so pass in null == any thread
					resumeBreakpoint = stepper.CreateBreakpoint(null, module, bpState.ResumeMethod.MDToken.Raw, bpState.ResumeOffset);
					var tcs = new TaskCompletionSource<DbgThread>();
					resumeBreakpoint.Hit += (s, e) => {
						bool hit = false;
						if (taskObjectId == null)
							hit = true;
						else {
							DbgDotNetValue taskObjId2 = null;
							try {
								taskObjId2 = TryGetTaskObjectId(e.Thread);
								hit = taskObjId2 == null || runtime.Equals(taskObjectId, taskObjId2);
							}
							finally {
								taskObjId2?.Dispose();
							}
						}
						if (hit) {
							e.Pause = true;
							tcs.TrySetResult(e.Thread);
						}
					};
					return tcs.Task;
				}
				finally {
					taskObjId?.Dispose();
				}
			}

			DbgDotNetValue TryGetTaskObjectId(DbgThread thread) {
				DbgEvaluationInfo evalInfo = null;
				DbgDotNetValue builderValue = null;
				try {
					evalInfo = owner.CreateEvaluationInfo(thread);
					builderValue = TaskEvalUtils.TryGetBuilder(evalInfo, builderFieldModule.GetReflectionModule(), builderFieldToken);
					if (builderValue == null)
						return null;
					return TaskEvalUtils.TryGetTaskObjectId(evalInfo, builderValue);
				}
				finally {
					evalInfo?.Close();
					builderValue?.Dispose();
				}
			}

			internal void ClearYieldBreakpoints() {
				var bps = yieldBreakpoints.Select(a => a.Breakpoint).ToArray();
				yieldBreakpoints.Clear();
				stepper.RemoveBreakpoints(bps);
			}

			internal void Dispose() {
				ClearYieldBreakpoints();
				if (resumeBreakpoint != null) {
					stepper.RemoveBreakpoints(new[] { resumeBreakpoint });
					resumeBreakpoint = null;
				}
				taskObjectId?.Dispose();
				taskObjectId = null;
			}
		}

		sealed class AsyncBreakpointState {
			internal readonly DbgDotNetStepperBreakpoint Breakpoint;
			internal readonly MethodDef ResumeMethod;
			internal readonly uint ResumeOffset;

			public event EventHandler<AsyncBreakpointState> Hit;

			public AsyncBreakpointState(DbgDotNetStepperBreakpoint yieldBreakpoint, MethodDef resumeMethod, uint resumeOffset) {
				Breakpoint = yieldBreakpoint;
				ResumeMethod = resumeMethod;
				ResumeOffset = resumeOffset;
				yieldBreakpoint.Hit += YieldBreakpoint_Hit;
			}

			void YieldBreakpoint_Hit(object sender, DbgDotNetStepperBreakpointEventArgs e) {
				Debug.Assert(Hit != null);
				e.Pause = true;
				Hit?.Invoke(this, this);
			}
		}

		static IEnumerable<DbgCodeRange[]> GetHiddenRanges(DbgCodeRange[] statements, ILSpan[] unusedSpans) {
#if DEBUG
			for (int i = 1; i < statements.Length; i++)
				Debug.Assert(statements[i - 1].End <= statements[i].Start);
			for (int i = 1; i < unusedSpans.Length; i++)
				Debug.Assert(unusedSpans[i - 1].End <= unusedSpans[i].Start);
#endif
			int si = 0;
			int ui = 0;
			while (si < statements.Length && ui < unusedSpans.Length) {
				while (ui < unusedSpans.Length && statements[si].End > unusedSpans[ui].Start)
					ui++;
				if (ui >= unusedSpans.Length)
					break;
				// If a hidden range immediately follows a normal statement, the hidden part could be the removed
				// async code and should be part of this statement.
				if (statements[si].End == unusedSpans[ui].Start)
					yield return new[] { new DbgCodeRange(unusedSpans[ui].Start, unusedSpans[ui].End) };
				si++;
			}
		}

		static void GetAsyncStepInfos(ref List<AsyncStepInfo> result, MethodDef currentMethod, AsyncMethodDebugInfo asyncInfo, DbgCodeRange[] ranges) {
			var stepInfos = asyncInfo.StepInfos;
			for (int i = 0; i < stepInfos.Length; i++) {
				ref readonly var stepInfo = ref stepInfos[i];
				if (Contains(currentMethod, ranges, stepInfo)) {
					if (result == null)
						result = new List<AsyncStepInfo>();
					result.Add(stepInfo);
				}
			}
		}

		static bool Contains(MethodDef currentMethod, DbgCodeRange[] ranges, in AsyncStepInfo stepInfo) {
			for (int i = 0; i < ranges.Length; i++) {
				ref readonly var range = ref ranges[i];
				if (range.Contains(stepInfo.YieldOffset) || (stepInfo.ResumeMethod == currentMethod && range.Contains(stepInfo.ResumeOffset)))
					return true;
			}
			return false;
		}

		async Task StepOutAsync(object tag) {
			runtime.Dispatcher.VerifyAccess();
			Debug.Assert(stepper.Session == null);
			try {
				var frame = stepper.TryGetFrameInfo(CurrentThread);
				if (frame == null) {
					// No frame? Just let the process run.
					stepper.Continue();
					return;
				}

				stepper.Session = stepper.CreateSession(tag);
				CurrentThread = await StepOutCoreAsync(frame);
				StepCompleted(null, tag);
			}
			catch (ForciblyCanceledException fce) {
				StepCompleted(fce.Message, tag);
			}
			catch (StepErrorException see) {
				StepError(see.Message, tag);
			}
			catch (Exception ex) {
				if (stepper.IgnoreException(ex))
					return;
				StepFailed(ex, tag);
			}
		}

		async Task<DbgThread> StepOutCoreAsync(DbgDotNetEngineStepperFrameInfo frame) {
			runtime.Dispatcher.VerifyAccess();
			Debug.Assert(stepper.Session != null);

			if (debuggerSettings.AsyncDebugging) {
				var result = await GetStepRangesAsync(frame, returnValues: false);
				if (result.DebugInfoOrNull?.AsyncInfo != null && result.DebugInfoOrNull.AsyncInfo.SetResultOffset != uint.MaxValue) {
					if (!frame.TryGetLocation(out var module, out var token, out _))
						throw new InvalidOperationException();
					// When the BP gets hit, we could be on a different thread, so pass in null
					var returnToAwaiterTask = TryCreateReturnToAwaiterTask(null, module, token, result.DebugInfoOrNull.AsyncInfo.SetResultOffset, result.DebugInfoOrNull.AsyncInfo.BuilderFieldOrNull?.MDToken.Raw ?? 0);
					if (returnToAwaiterTask != null) {
						stepper.Continue();
						return await returnToAwaiterTask;
					}
				}
			}

			return await stepper.StepOutAsync(frame);
		}

		readonly struct GetStepRangesAsyncResult {
			public MethodDebugInfo DebugInfoOrNull { get; }
			public MethodDebugInfo StateMachineDebugInfoOrNull { get; }
			public DbgDotNetEngineStepperFrameInfo Frame { get; }
			public DbgCodeRange[] StatementRanges { get; }
			public DbgCodeRange[] ExactStatementRanges { get; }
			public DbgILInstruction[][] StatementInstructions { get; }
			public GetStepRangesAsyncResult(MethodDebugInfo debugInfo, MethodDebugInfo stateMachineDebugInfoOrNull, DbgDotNetEngineStepperFrameInfo frame, DbgCodeRange[] statementRanges, DbgCodeRange[] exactStatementRanges, DbgILInstruction[][] statementInstructions) {
				DebugInfoOrNull = debugInfo;
				StateMachineDebugInfoOrNull = stateMachineDebugInfoOrNull;
				Frame = frame ?? throw new ArgumentNullException(nameof(frame));
				StatementRanges = statementRanges ?? throw new ArgumentNullException(nameof(statementRanges));
				ExactStatementRanges = exactStatementRanges ?? throw new ArgumentNullException(nameof(exactStatementRanges));
				StatementInstructions = statementInstructions ?? throw new ArgumentNullException(nameof(statementInstructions));
			}
		}

		async Task<GetStepRangesAsyncResult> GetStepRangesAsync(DbgDotNetEngineStepperFrameInfo frame, bool returnValues) {
			runtime.Dispatcher.VerifyAccess();
			if (!frame.TryGetLocation(out var module, out uint token, out uint offset))
				throw new StepErrorException("Internal error");

			uint continueCounter = stepper.ContinueCounter;
			var info = await dbgDotNetDebugInfoService.GetMethodDebugInfoAsync(module, token, offset);
			if (continueCounter != stepper.ContinueCounter)
				throw new StepErrorException("Internal error");

			var codeRanges = Array.Empty<DbgCodeRange>();
			var exactCodeRanges = Array.Empty<DbgCodeRange>();
			var instructions = Array.Empty<DbgILInstruction[]>();
			if (info.DebugInfoOrNull != null) {
				var sourceStatement = info.DebugInfoOrNull.GetSourceStatementByCodeOffset(offset);
				ILSpan[] ranges;
				if (sourceStatement == null)
					ranges = info.DebugInfoOrNull.GetUnusedRanges();
				else {
					var sourceStatements = info.DebugInfoOrNull.GetILSpansOfStatement(sourceStatement.Value.TextSpan);
					Debug.Assert(sourceStatements.Any(a => a == sourceStatement.Value.ILSpan));
					exactCodeRanges = CreateStepRanges(sourceStatements);
					ranges = info.DebugInfoOrNull.GetRanges(sourceStatements);
				}

				codeRanges = CreateStepRanges(ranges);
				if (returnValues && debuggerSettings.ShowReturnValues && frame.SupportsReturnValues)
					instructions = GetInstructions(info.DebugInfoOrNull.Method, exactCodeRanges) ?? Array.Empty<DbgILInstruction[]>();
			}
			if (codeRanges.Length == 0)
				codeRanges = new[] { new DbgCodeRange(offset, offset + 1) };
			if (exactCodeRanges.Length == 0)
				exactCodeRanges = new[] { new DbgCodeRange(offset, offset + 1) };
			return new GetStepRangesAsyncResult(info.DebugInfoOrNull, info.StateMachineDebugInfoOrNull, frame, codeRanges, exactCodeRanges, instructions);
		}

		static DbgILInstruction[][] GetInstructions(MethodDef method, DbgCodeRange[] ranges) {
			var body = method.Body;
			if (body == null)
				return null;
			var instrs = body.Instructions;
			int instrsIndex = 0;

			var res = new DbgILInstruction[ranges.Length][];
			var list = new List<DbgILInstruction>();
			for (int i = 0; i < res.Length; i++) {
				list.Clear();

				ref readonly var span = ref ranges[i];
				uint start = span.Start;
				uint end = span.End;

				while (instrsIndex < instrs.Count && instrs[instrsIndex].Offset < start)
					instrsIndex++;
				while (instrsIndex < instrs.Count && instrs[instrsIndex].Offset < end) {
					var instr = instrs[instrsIndex];
					list.Add(new DbgILInstruction(instr.Offset, (ushort)instr.OpCode.Code, (instr.Operand as IMDTokenProvider)?.MDToken.Raw ?? 0));
					instrsIndex++;
				}

				res[i] = list.ToArray();
			}
			return res;
		}

		static DbgCodeRange[] CreateStepRanges(ILSpan[] ilSpans) {
			if (ilSpans.Length == 0)
				return Array.Empty<DbgCodeRange>();
			var stepRanges = new DbgCodeRange[ilSpans.Length];
			for (int i = 0; i < stepRanges.Length; i++) {
				ref readonly var span = ref ilSpans[i];
				stepRanges[i] = new DbgCodeRange(span.Start, span.End);
			}
			return stepRanges;
		}

		void StepCompleted(string forciblyCanceledErrorMessage, object tag) {
			runtime.Dispatcher.VerifyAccess();
			if (stepper.Session == null || stepper.Session.Tag != tag)
				return;
			if (forciblyCanceledErrorMessage == null)
				stepper.OnStepComplete();
			stepper.Session = null;
			RaiseStepComplete(tag, forciblyCanceledErrorMessage, forciblyCanceled: forciblyCanceledErrorMessage != null);
		}

		void StepError(string errorMessage, object tag) {
			runtime.Dispatcher.VerifyAccess();
			if (stepper.Session == null || stepper.Session.Tag != tag)
				return;
			stepper.Session = null;
			RaiseStepComplete(tag, errorMessage);
		}

		void StepFailed(Exception exception, object tag) {
			runtime.Dispatcher.VerifyAccess();
			StepError("Internal error: " + exception.Message, tag);
		}

		public override void Cancel(object tag) => runtime.Dispatcher.BeginInvoke(() => Cancel_EngineThread(tag));
		void Cancel_EngineThread(object tag) {
			runtime.Dispatcher.VerifyAccess();
			var oldStepperData = stepper.Session;
			if (oldStepperData == null)
				return;
			if (oldStepperData.Tag != tag)
				return;
			ForceCancel_EngineThread();
		}

		void CleanUp() {
			ClearReturnToAwaiterState();
			ClearStepIntoState();
			SetAsyncStepOverState(null);
		}

		void ForceCancel_EngineThread() {
			runtime.Dispatcher.VerifyAccess();
			CleanUp();
			var oldSession = stepper.Session;
			stepper.Session = null;
			if (oldSession != null)
				stepper.OnCanceled(oldSession);
		}

		protected override void CloseCore(DbgDispatcher dispatcher) {
			if (stepper.Session != null)
				runtime.Dispatcher.BeginInvoke(() => ForceCancel_EngineThread());
			stepper.Close(dispatcher);
		}
	}
}

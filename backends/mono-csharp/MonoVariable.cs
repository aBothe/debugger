using System;
using Mono.CSharp.Debugger;
using Mono.Debugger.Backends;

namespace Mono.Debugger.Languages.CSharp
{
	internal class MonoVariable : IVariable
	{
		VariableInfo info;
		string name;
		MonoType type;
		DebuggerBackend backend;
		TargetAddress start_scope, end_scope;
		bool is_local;

		public MonoVariable (DebuggerBackend backend, string name, MonoType type,
				     bool is_local, IMethod method, VariableInfo info)
		{
			this.backend = backend;
			this.name = name;
			this.type = type;
			this.is_local = is_local;
			this.info = info;

			if (info.BeginScope != 0)
				start_scope = method.StartAddress + info.BeginScope;
			else
				start_scope = method.MethodStartAddress;
			if (info.EndScope != 0)
				end_scope = method.StartAddress + info.EndScope;
			else
				end_scope = method.MethodEndAddress;
		}

		public DebuggerBackend Backend {
			get {
				return backend;
			}
		}

		public string Name {
			get {
				return name;
			}
		}

		public ITargetType Type {
			get {
				return type;
			}
		}

		internal VariableInfo VariableInfo {
			get {
				return info;
			}
		}

		public TargetAddress StartScope {
			get {
				return start_scope;
			}
		}

		public TargetAddress EndScope {
			get {
				return end_scope;
			}
		}

		public ITargetObject GetObject (StackFrame frame)
		{
			MonoTargetLocation location;
			if (info.Mode == VariableInfo.AddressMode.Register)
				if (frame.Level != 0)
					throw new LocationInvalidException ();
				location = new MonoRegisterLocation (
					backend, frame, type.IsByRef, info.Index, info.Offset,
					start_scope, end_scope);
			else if (info.Mode == VariableInfo.AddressMode.Stack)
				location = new MonoStackLocation (
					backend, frame, type.IsByRef, is_local, info.Offset, 0,
					start_scope, end_scope);
			else
				throw new LocationInvalidException ();

			return type.GetObject (location);
		}

		public override string ToString ()
		{
			return String.Format ("MonoVariable [{0}:{1}:{2}:{3}]",
					      Name, Type, StartScope, EndScope);
		}
	}
}

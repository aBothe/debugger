using System;
using System.Diagnostics;

namespace Mono.Debugger.Languages
{
	public abstract class TargetStructType : TargetType
	{
		protected TargetStructType (Language language, TargetObjectKind kind)
			: base (language, kind)
		{ }

		public abstract string BaseName {
			get;
		}

		public abstract Module Module {
			get;
		}

		public abstract bool HasParent {
			get;
		}

		public virtual DebuggerDisplayAttribute DebuggerDisplayAttribute {
			get { return null; }
		}

		public virtual DebuggerTypeProxyAttribute DebuggerTypeProxyAttribute {
			get { return null; }
		}

		internal abstract TargetStructType GetParentType (TargetMemoryAccess target);

		public TargetStructType GetParentType (Thread thread)
		{
			return (TargetStructType) thread.ThreadServant.DoTargetAccess (
				delegate (TargetMemoryAccess target) {
					return GetParentType (target);
			});
		}

		public abstract bool IsClassInitialized {
			get;
		}

		internal abstract TargetClass GetClass (TargetMemoryAccess target);

		public TargetClass GetClass (Thread thread)
		{
			return (TargetClass) thread.ThreadServant.DoTargetAccess (
				delegate (TargetMemoryAccess target) {
					return GetClass (target);
			});
		}

		public virtual TargetClass ForceClassInitialization (Thread thread)
		{
			return GetClass (thread);
		}
	}
}

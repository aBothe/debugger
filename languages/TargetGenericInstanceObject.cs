namespace Mono.Debugger.Languages
{
	public abstract class TargetGenericInstanceObject : TargetStructObject
	{
		public readonly new TargetGenericInstanceType Type;

		protected TargetGenericInstanceObject (TargetGenericInstanceType type,
						       TargetLocation location)
			: base (type, location)
		{
			this.Type = type;
		}

		public abstract TargetObject GetParentObject (Thread thread);

		public abstract TargetObject GetField (Thread thread, TargetFieldInfo field);
	}
}

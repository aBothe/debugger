namespace Mono.Debugger.Languages
{
	public abstract class TargetGenericParameterType : TargetType
	{
		protected TargetGenericParameterType (Language language)
			: base (language, TargetObjectKind.GenericParameter)
		{ }
	}
}

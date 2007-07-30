using System;
using Cecil = Mono.Cecil;

namespace Mono.Debugger.Languages.Mono
{
	internal class MonoObjectType : TargetObjectType
	{
		MonoSymbolFile file;
		MonoClassType class_type;

		public MonoObjectType (MonoSymbolFile file, Cecil.TypeDefinition typedef, int size)
			: base (file.MonoLanguage, "object", size)
		{
			this.file = file;

			class_type = new MonoClassType (file, typedef);
		}

		public MonoSymbolFile File {
			get { return file; }
		}

		public override TargetClassType ClassType {
			get { return class_type; }
		}

		internal override TargetObject GetObject (TargetLocation location)
		{
			return new MonoObjectObject (this, location);
		}

		public override TargetPointerObject GetObject (TargetAddress address)
		{
			return new MonoObjectObject (this, new AbsoluteTargetLocation (address));
		}
	}
}
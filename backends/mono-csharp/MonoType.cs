using System;

namespace Mono.Debugger.Languages.CSharp
{
	internal abstract class MonoType : ITargetType
	{
		protected Type type;
		protected ITargetMemoryReader info;

		bool has_fixed_size;
		int size;

		protected MonoType (Type type, int size)
		{
			this.type = type;
			this.size = size;
			this.has_fixed_size = true;
		}

		protected MonoType (Type type, int size, bool has_fixed_size, ITargetMemoryReader info)
		{
			this.type = type;
			this.size = size;
			this.info = info;
			this.has_fixed_size = has_fixed_size;
		}

		public static MonoType GetType (Type type, ITargetMemoryAccess memory, TargetAddress address)
		{
			int size = memory.ReadInteger (address);
			ITargetMemoryReader info = memory.ReadMemory (
				address + memory.TargetIntegerSize, size);
			return GetType (type, info);
		}

		public static MonoType GetType (Type type, ITargetMemoryReader info)
		{
			int size = info.ReadInteger ();
			if (size > 0) {
				if (MonoFundamentalType.Supports (type))
					return new MonoFundamentalType (type, size);
				else
					return new MonoOpaqueType (type, size);
			} else if (size == -1)
				return new MonoOpaqueType (type, 0);

			size = info.BinaryReader.ReadInt32 ();

			int kind = info.ReadByte ();
			switch (kind) {
			case 1:
				return new MonoStringType (type, size, info);

			case 2:
				return new MonoArrayType (type, size, info, false);

			case 3:
				return new MonoArrayType (type, size, info, true);

			case 4:
				return new MonoEnumType (type, size, info);

			case 5:
				return new MonoStructType (type, size, info);

			case 6:
				return new MonoClassType (type, size, info);

			default:
				return new MonoOpaqueType (type, size);
			}
		}

		internal virtual TargetAddress GetAddress (ITargetLocation location,
							   out ITargetMemoryAccess memory)
		{
			TargetAddress address = location.Address;
			StackFrame frame = location.Handle as StackFrame;
			if (frame == null)
				throw new LocationInvalidException ();

			memory = frame.TargetMemoryAccess;
			if (IsByRef)
				address = memory.ReadAddress (address);

			return address;
		}

		public object TypeHandle {
			get {
				return type;
			}
		}

		public abstract bool IsByRef {
			get;
		}

		public virtual bool HasFixedSize {
			get {
				return has_fixed_size;
			}
		}

		public virtual int Size {
			get {
				return size;
			}
		}

		public abstract bool HasObject {
			get;
		}

		public abstract MonoObject GetObject (ITargetLocation location);

		public override string ToString ()
		{
			return String.Format ("{0} [{1}:{2}:{3}:{4}:{5}]", GetType (), TypeHandle,
					      IsByRef, HasFixedSize, Size, HasObject);
		}
	}
}

using System;

namespace Mono.Debugger
{
	public interface ITargetArrayType
	{
		// <summary>
		//   The array's element type.  For multi-dimensional arrays,
		//   this'll return the array type itself unless this is the
		//   last dimension.
		// </summary>
		ITargetType ElementType {
			get;
		}
	}
}

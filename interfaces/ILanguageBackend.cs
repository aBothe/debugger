using System;

namespace Mono.Debugger
{
	public interface ILanguageBackend
	{
		ISymbolTable SymbolTable {
			get;
		}

		void UpdateSymbolTable ();

		// <summary>
		//   The address of the JIT's generic trampoline code.
		// </summary>
		ITargetLocation GenericTrampolineCode {
			get;
		}

		ITargetLocation GetTrampoline (ITargetLocation address);
	}
}


using System;
using NUnit.Framework;

using Mono.Debugger;
using Mono.Debugger.Languages;
using Mono.Debugger.Frontend;
using Mono.Debugger.Test.Framework;

namespace Mono.Debugger.Tests
{
	[DebuggerTestFixture]
	public class TestEnum : DebuggerTestFixture
	{
		public TestEnum ()
			: base ("TestEnum")
		{ }

		[Test]
		[Category("ManagedTypes")]
		public void Main ()
		{
			Process process = Start ();
			Assert.IsTrue (process.IsManaged);
			Assert.IsTrue (process.MainThread.IsStopped);
			Thread thread = process.MainThread;

			const int line_main = 50;
			const int line_main_2 = 54;

			AssertStopped (thread, "X.Main()", line_main);

			int bpt_main_2 = AssertBreakpoint (line_main_2);
			AssertExecute ("continue");
			AssertHitBreakpoint (thread, bpt_main_2, "X.Main()", line_main_2);

			AssertPrint (thread, DisplayFormat.HexaDecimal, "irish_pub_thursday",
				     "(Pub) { Music = 0x0, Drinks = 0x301f }");
			AssertPrint (thread, "irish_pub_thursday.Music", "(Music) Irish");
			AssertPrint (thread, "irish_pub_thursday.Drinks",
				     "(Drinks) Alcoholic | All | Beer | Coffee | Juice | " +
				     "NonAlcoholic | Rum | Tea | Tequila | Vodka | Water | Whine");
			AssertPrint (thread, DisplayFormat.HexaDecimal, "lunch_break",
				     "(Pub) { Music = 0x2, Drinks = 0x1005 }");
			AssertPrint (thread, "lunch_break",
				     "(Pub) { Music = RockPop, Drinks = Coffee | Water }");
			AssertPrint (thread, DisplayFormat.HexaDecimal, "dinner",
				     "(Pub) { Music = 0x1, Drinks = 0x100a }");
			AssertPrint (thread, "dinner",
				     "(Pub) { Music = Country, Drinks = Juice | Tea }");

			AssertExecute ("continue");
			AssertTargetOutput ("Pub");
			AssertTargetOutput ("Pub");
			AssertTargetOutput ("Pub");
			AssertTargetExited (thread.Process);
		}
	}
}

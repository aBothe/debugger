using System;
using System.Text;
using System.IO;
using System.Reflection;
using System.Collections;
using System.Globalization;
using Mono.Debugger;

namespace Mono.Debugger.Frontends.CommandLine
{
	public abstract class Command
	{
		protected abstract void DoExecute (ScriptingContext context);

		public void Execute (ScriptingContext context)
		{
			try {
				DoExecute (context);
			} catch (ScriptingException ex) {
				context.Error (ex);
			}
		}
	}

	public abstract class Expression
	{
		protected abstract object DoResolve (ScriptingContext context);

		public object Resolve (ScriptingContext context)
		{
			object retval = DoResolve (context);

			if (retval == null)
				throw new ScriptingException ("Can't resolve command: {0}", this);

			return retval;
		}
	}

	public class PrintCommand : Command
	{
		Expression expression;

		public PrintCommand (Expression expression)
		{
			this.expression = expression;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			object retval = expression.Resolve (context);

			if (retval is long)
				context.Print (String.Format ("0x{0:x}", (long) retval));
			else
				context.Print (retval);
		}
	}

	public class PrintVariableCommand : Command
	{
		VariableExpression var_expr;

		public PrintVariableCommand (VariableExpression var_expr)
		{
			this.var_expr = var_expr;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ITargetObject tobj = var_expr.ResolveVariable (context);

			if (tobj is ITargetFundamentalObject) {
				ITargetFundamentalObject fobj = (ITargetFundamentalObject) tobj;

				if (fobj.HasObject) {
					context.Print (fobj.Object);
					return;
				}
			}

			context.Print (tobj);
		}
	}

	public class FrameCommand : Command
	{
		int number;
		ProcessExpression process_expr;

		public FrameCommand (ProcessExpression process_expr, int number)
		{
			this.process_expr = process_expr;
			this.number = number;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			process.PrintFrame (number);
		}
	}

	public class DisassembleCommand : Command
	{
		int number;
		ProcessExpression process_expr;

		public DisassembleCommand (ProcessExpression process_expr, int number)
		{
			this.process_expr = process_expr;
			this.number = number;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			process.Disassemble (number);
		}
	}

	public class DisassembleMethodCommand : Command
	{
		int number;
		ProcessExpression process_expr;

		public DisassembleMethodCommand (ProcessExpression process_expr, int number)
		{
			this.process_expr = process_expr;
			this.number = number;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			process.DisassembleMethod (number);
		}
	}

	public class StartCommand : Command
	{
		string[] args;
		int pid;

		public StartCommand (string[] args)
		{
			this.args = args;
			this.pid = -1;
		}

		public StartCommand (string[] args, int pid)
		{
			this.args = args;
			this.pid = pid;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			context.Start (args, pid);
		}
	}

	public class SelectProcessCommand : Command
	{
		ProcessExpression process_expr;

		public SelectProcessCommand (ProcessExpression process_expr)
		{
			this.process_expr = process_expr;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);
			context.CurrentProcess = process;
		}
	}

	public class BackgroundProcessCommand : Command
	{
		ProcessExpression process_expr;

		public BackgroundProcessCommand (ProcessExpression process_expr)
		{
			this.process_expr = process_expr;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);
			process.Background ();
		}
	}

	public class StopProcessCommand : Command
	{
		ProcessExpression process_expr;

		public StopProcessCommand (ProcessExpression process_expr)
		{
			this.process_expr = process_expr;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);
			process.Stop ();
		}
	}

	public class StepCommand : Command
	{
		ProcessExpression process_expr;
		WhichStepCommand which;

		public StepCommand (ProcessExpression process_expr, WhichStepCommand which)
		{
			this.process_expr = process_expr;
			this.which = which;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);
			process.Step (which);
		}
	}

	public class BacktraceCommand : Command
	{
		ProcessExpression process_expr;

		public BacktraceCommand (ProcessExpression process_expr)
		{
			this.process_expr = process_expr;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			int current_idx = process.CurrentFrameIndex;
			Backtrace backtrace = process.GetBacktrace ();
			for (int i = 0; i < backtrace.Length; i++) {
				string prefix = i == current_idx ? "(*)" : "   ";
				context.Print ("{0} {1}", prefix, backtrace [i]);
			}
		}
	}

	public class UpCommand : Command
	{
		ProcessExpression process_expr;

		public UpCommand (ProcessExpression process_expr)
		{
			this.process_expr = process_expr;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			process.CurrentFrameIndex++;
			process.PrintFrame ();
		}
	}

	public class DownCommand : Command
	{
		ProcessExpression process_expr;

		public DownCommand (ProcessExpression process_expr)
		{
			this.process_expr = process_expr;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			process.CurrentFrameIndex--;
			process.PrintFrame ();
		}
	}

	public class ShowProcessesCommand : Command
	{
		protected override void DoExecute (ScriptingContext context)
		{
			if (!context.HasTarget) {
				context.Print ("No target.");
				return;
			}

			int current_id = context.CurrentProcess.ID;
			foreach (ProcessHandle proc in context.Processes) {
				string prefix = proc.ID == current_id ? "(*)" : "   ";
				context.Print ("{0} {1}", prefix, proc);
			}
		}
	}

	public class ShowRegistersCommand : Command
	{
		ProcessExpression process_expr;
		int number;

		public ShowRegistersCommand (ProcessExpression process_expr, int number)
		{
			this.process_expr = process_expr;
			this.number = number;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			string[] names = process.Architecture.RegisterNames;
			int[] indices = process.Architecture.RegisterIndices;

			foreach (Register register in process.GetRegisters (number, indices))
				context.Print ("%{0} = 0x{1:x}", names [register.Index], register.Data);
		}
	}

	public class ShowParametersCommand : Command
	{
		ProcessExpression process_expr;
		int number;

		public ShowParametersCommand (ProcessExpression process_expr, int number)
		{
			this.process_expr = process_expr;
			this.number = number;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			process.ShowParameters (number);
		}
	}

	public class ShowLocalsCommand : Command
	{
		ProcessExpression process_expr;
		int number;

		public ShowLocalsCommand (ProcessExpression process_expr, int number)
		{
			this.process_expr = process_expr;
			this.number = number;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			process.ShowLocals (number);
		}
	}

	public class ShowVariableTypeCommand : Command
	{
		VariableExpression var_expr;

		public ShowVariableTypeCommand (VariableExpression var_expr)
		{
			this.var_expr = var_expr;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ITargetType type = var_expr.ResolveType (context);

			context.ShowVariableType (type, var_expr.Name);
		}
	}

	public class BreakCommand : Command
	{
		ProcessExpression process_expr;
		string identifier;
		int line;

		public BreakCommand (ProcessExpression process_expr, string identifier)
		{
			this.process_expr = process_expr;
			this.identifier = identifier;
			this.line = -1;
		}

		public BreakCommand (ProcessExpression process_expr, string identifier, int line)
		{
			this.process_expr = process_expr;
			this.identifier = identifier;
			this.line = line;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);
			if (line != -1)
				process.InsertBreakpoint (identifier, line);
			else
				process.InsertBreakpoint (identifier);
		}
	}

	public class ScriptingVariableAssignCommand : Command
	{
		string identifier;
		VariableExpression expr;

		public ScriptingVariableAssignCommand (string identifier, VariableExpression expr)
		{
			this.identifier = identifier;
			this.expr = expr;
		}

		protected override void DoExecute (ScriptingContext context)
		{
			context [identifier] = expr;
		}
	}

	public class ProcessExpression : Expression
	{
		int number;

		public ProcessExpression (int number)
		{
			this.number = number;
		}

		protected override object DoResolve (ScriptingContext context)
		{
			if (number == -1)
				return context.CurrentProcess;

			foreach (ProcessHandle proc in context.Processes)
				if (proc.ID == number)
					return proc;

			throw new ScriptingException ("No such process: {0}", number);
		}

		public override string ToString ()
		{
			return String.Format ("{0} ({1})", GetType (), number);
		}
	}

	public class RegisterExpression : Expression
	{
		int number;
		ProcessExpression process_expr;
		string register;

		public RegisterExpression (ProcessExpression process_expr, int number, string register)
		{
			this.process_expr = process_expr;
			this.number = number;
			this.register = register;
		}

		protected override object DoResolve (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			return process.GetRegister (number, register);
		}

		public override string ToString ()
		{
			return String.Format ("{0} ({1},{2},{3})", GetType (), process_expr, number,
					      register);
		}
	}

	public abstract class VariableExpression : Expression
	{
		public abstract string Name {
			get;
		}

		protected abstract ITargetObject DoResolveVariable (ScriptingContext context);

		public ITargetObject ResolveVariable (ScriptingContext context)
		{
			ITargetObject retval = DoResolveVariable (context);
			if (retval == null)
				throw new ScriptingException ("Can't resolve variable: {0}", this);

			return retval;
		}

		protected abstract ITargetType DoResolveType (ScriptingContext context);

		public ITargetType ResolveType (ScriptingContext context)
		{
			ITargetType type = DoResolveType (context);
			if (type == null)
				throw new ScriptingException ("Can't get type of variable {0}.", this);

			return type;
		}

		protected override object DoResolve (ScriptingContext context)
		{
			return ResolveVariable (context);
		}

		public override string ToString ()
		{
			return String.Format ("{0} ({1})", GetType (), Name);
		}
	}

	public class ScriptingVariableReference : VariableExpression
	{
		string identifier;

		public ScriptingVariableReference (string identifier)
		{
			this.identifier = identifier;
		}

		public override string Name {
			get { return '!' + identifier; }
		}

		protected override ITargetObject DoResolveVariable (ScriptingContext context)
		{
			VariableExpression expr = context [identifier];
			if (expr == null)
				return null;

			return expr.ResolveVariable (context);
		}

		protected override ITargetType DoResolveType (ScriptingContext context)
		{
			VariableExpression expr = context [identifier];
			if (expr == null)
				return null;

			return expr.ResolveType (context);
		}
	}

	public class VariableReferenceExpression : VariableExpression
	{
		int number;
		ProcessExpression process_expr;
		string identifier;

		public VariableReferenceExpression (ProcessExpression process_expr, int number,
						    string identifier)
		{
			this.process_expr = process_expr;
			this.number = number;
			this.identifier = identifier;
		}

		public override string Name {
			get { return '$' + identifier; }
		}

		protected override ITargetObject DoResolveVariable (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			return process.GetVariable (number, identifier);
		}

		protected override ITargetType DoResolveType (ScriptingContext context)
		{
			ProcessHandle process = (ProcessHandle) process_expr.Resolve (context);

			return process.GetVariableType (number, identifier);
		}

		public override string ToString ()
		{
			return String.Format ("{0} ({1},{2},{3})", GetType (), process_expr, number,
					      identifier);
		}
	}

	public class StructAccessExpression : VariableExpression
	{
		VariableExpression var_expr;
		string identifier;

		public StructAccessExpression (VariableExpression var_expr, string identifier)
		{
			this.var_expr = var_expr;
			this.identifier = identifier;
		}

		public override string Name {
			get {
				return String.Format ("{0}.{1}", var_expr.Name, identifier);
			}
		}

		ITargetFieldInfo get_field (ITargetStructType tstruct)
		{
			foreach (ITargetFieldInfo field in tstruct.Fields)
				if (field.Name == identifier)
					return field;

			throw new ScriptingException ("Variable {0} has no field {1}.", var_expr.Name,
						      identifier);
		}

		protected override ITargetObject DoResolveVariable (ScriptingContext context)
		{
			ITargetStructObject sobj = var_expr.ResolveVariable (context) as ITargetStructObject;
			if (sobj == null)
				throw new ScriptingException ("Variable {0} is not a struct or class type.",
							      var_expr.Name);

			ITargetFieldInfo field = get_field (sobj.Type);
			return sobj.GetField (field.Index);
		}

		protected override ITargetType DoResolveType (ScriptingContext context)
		{
			ITargetStructType tstruct = var_expr.ResolveType (context) as ITargetStructType;
			if (tstruct == null)
				throw new ScriptingException ("Variable {0} is not a struct or class type.",
							      var_expr.Name);

			ITargetFieldInfo field = get_field (tstruct);
			return field.Type;
		}
	}

	public class VariableDereferenceExpression : VariableExpression
	{
		VariableExpression var_expr;

		public VariableDereferenceExpression (VariableExpression var_expr)
		{
			this.var_expr = var_expr;
		}

		public override string Name {
			get {
				return '*' + var_expr.Name;
			}
		}

		protected override ITargetObject DoResolveVariable (ScriptingContext context)
		{
			ITargetPointerObject pobj = var_expr.ResolveVariable (context) as ITargetPointerObject;
			if (pobj == null)
				throw new ScriptingException ("Variable {0} is not a pointer type.",
							      var_expr.Name);

			if (!pobj.HasDereferencedObject)
				throw new ScriptingException ("Cannot dereference {0}.", var_expr.Name);

			return pobj.DereferencedObject;
		}

		protected override ITargetType DoResolveType (ScriptingContext context)
		{
			ITargetPointerObject pobj = var_expr.ResolveVariable (context) as ITargetPointerObject;
			if (pobj == null)
				throw new ScriptingException ("Variable {0} is not a pointer type.",
							      var_expr.Name);

			ITargetType type = pobj.CurrentType;
			if (type == null)
				throw new ScriptingException ("Cannot get current type of {0}.", var_expr.Name);

			return type;
		}
	}
}

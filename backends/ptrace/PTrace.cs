using GLib;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Configuration;
using System.Globalization;
using System.Reflection;
using System.Threading;
using System.Diagnostics;
using System.Collections;
using System.Collections.Specialized;
using System.Runtime.InteropServices;

using Mono.Debugger;
using Mono.Debugger.Languages;
using Mono.Debugger.Architecture;
using Mono.CSharp.Debugger;

namespace Mono.Debugger.Backends
{
	internal enum ChildMessage {
		CHILD_EXITED = 1,
		CHILD_STOPPED,
		CHILD_SIGNALED
	}

	internal enum CommandError {
		NONE = 0,
		IO,
		UNKNOWN,
		INVALID_COMMAND,
		NOT_STOPPED
	}
	
	internal enum ServerCommand {
		GET_PC = 1,
		DETACH,
		SHUTDOWN,
		KILL,
		CONTINUE,
		STEP
	}

	internal delegate void ChildSetupHandler ();
	internal delegate void ChildExitedHandler ();
	internal delegate void ChildMessageHandler (ChildMessage message, int arg);

	internal class MonoDebuggerInfo
	{
		public readonly ITargetLocation trampoline_code;
		public readonly ITargetLocation symbol_file_generation;
		public readonly ITargetLocation symbol_file_table;
		public readonly ITargetLocation update_symbol_file_table;
		public readonly ITargetLocation compile_method;

		internal MonoDebuggerInfo (ITargetMemoryReader reader)
		{
			reader.Offset = reader.TargetLongIntegerSize +
				2 * reader.TargetIntegerSize;
			trampoline_code = reader.ReadAddress ();
			symbol_file_generation = reader.ReadAddress ();
			symbol_file_table = reader.ReadAddress ();
			update_symbol_file_table = reader.ReadAddress ();
			compile_method = reader.ReadAddress ();
		}

		public override string ToString ()
		{
			return String.Format ("MonoDebuggerInfo ({0:x}, {1:x}, {2:x}, {3:x}, {4:x})",
					      trampoline_code, symbol_file_generation, symbol_file_table,
					      update_symbol_file_table, compile_method);
		}
	}

	internal class Inferior : IInferior, ITargetMemoryAccess, IDisposable
	{
		IntPtr server_handle;
		IOOutputChannel inferior_stdin;
		IOInputChannel inferior_stdout;
		IOInputChannel inferior_stderr;

		string working_directory;
		string[] argv;
		string[] envp;

		BfdSymbolTable bfd_symtab;

		bool attached;

		int child_pid;

		ITargetInfo target_info;

		public int PID {
			get {
				return child_pid;
			}
		}

		MonoDebuggerInfo mono_debugger_info = null;
		public MonoDebuggerInfo MonoDebuggerInfo {
			get {
				if (mono_debugger_info != null)
					return mono_debugger_info;

				read_mono_debugger_info ();
				return mono_debugger_info;
			}
		}

		public event ChildExitedHandler ChildExited;
		public event ChildMessageHandler ChildMessage;

		[DllImport("monodebuggerserver")]
		static extern bool mono_debugger_spawn_async (string working_directory, string[] argv, string[] envp, bool search_path, ChildSetupHandler child_setup, out int child_pid, out IntPtr status_channel, out IntPtr server_handle, ChildExitedHandler child_exited, ChildMessageHandler child_message, out int standard_input, out int standard_output, out int standard_error, out IntPtr errout);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_send_command (IntPtr handle, ServerCommand command);

		[DllImport("monodebuggerserver")]
		static extern bool mono_debugger_server_read_uint64 (IntPtr handle, out long arg);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_read_memory (IntPtr handle, long start, int size, out IntPtr data);

		[DllImport("monodebuggerserver")]
		static extern CommandError mono_debugger_server_get_target_info (IntPtr handle, out int target_int_size, out int target_long_size, out int target_address_size);

		[DllImport("monodebuggerglue")]
		static extern void mono_debugger_glue_kill_process (int pid, bool force);

		[DllImport("glib-2.0")]
		extern static void g_free (IntPtr data);

		void handle_error (CommandError error)
		{
			switch (error) {
			case CommandError.NONE:
				return;

			case CommandError.NOT_STOPPED:
				throw new TargetNotStoppedException ();

			default:
				throw new TargetException (
					"Got unknown error condition from inferior: " + error);
			}
		}

		void send_command (ServerCommand command)
		{
			CommandError result = mono_debugger_server_send_command (server_handle, command);

			handle_error (result);
		}

		long read_long ()
		{
			long retval;
			if (!mono_debugger_server_read_uint64 (server_handle, out retval))
				throw new TargetException (
					"Can't read ulong argument from inferior");

			return retval;
		}

		public Inferior (string working_directory, string[] argv, string[] envp)
		{
			this.working_directory = working_directory;
			this.argv = argv;
			this.envp = envp;

			int stdin_fd, stdout_fd, stderr_fd;
			IntPtr status_channel, error;

			string[] my_argv = new string [argv.Length + 5];
			my_argv [0] = "mono-debugger-server";
			my_argv [1] = OffsetTable.Magic.ToString ("x");
			my_argv [2] = OffsetTable.Version.ToString ();
			my_argv [3] = "0";
			my_argv [4] = working_directory;
			argv.CopyTo (my_argv, 5);

			bfd_symtab = new BfdSymbolTable (argv [0]);

			bool retval = mono_debugger_spawn_async (
				working_directory, my_argv, envp, true, null, out child_pid,
				out status_channel, out server_handle,
				new ChildExitedHandler (child_exited),
				new ChildMessageHandler (child_message),
				out stdin_fd, out stdout_fd,
				out stderr_fd, out error);

			if (!retval)
				throw new Exception ();

			inferior_stdin = new IOOutputChannel (stdin_fd);
			inferior_stdout = new IOInputChannel (stdout_fd);
			inferior_stderr = new IOInputChannel (stderr_fd);

			setup_inferior ();
		}

		public Inferior (int pid, string[] envp)
		{
			this.envp = envp;

			int stdin_fd, stdout_fd, stderr_fd;
			IntPtr status_channel, error;

			string[] my_argv = { "mono-debugger-server",
					     OffsetTable.Magic.ToString ("x"),
					     OffsetTable.Version.ToString (),
					     pid.ToString ()
			};

			bfd_symtab = new BfdSymbolTable (argv [0]);

			bool retval = mono_debugger_spawn_async (
				working_directory, my_argv, envp, true, null, out child_pid,
				out status_channel, out server_handle,
				new ChildExitedHandler (child_exited),
				new ChildMessageHandler (child_message),
				out stdin_fd, out stdout_fd,
				out stderr_fd, out error);

			if (!retval)
				throw new Exception ();

			inferior_stdin = new IOOutputChannel (stdin_fd);
			inferior_stdout = new IOInputChannel (stdout_fd);
			inferior_stderr = new IOInputChannel (stderr_fd);

			setup_inferior ();
		}

		void setup_inferior ()
		{
			inferior_stdout.ReadLine += new ReadLineHandler (inferior_output);
			inferior_stderr.ReadLine += new ReadLineHandler (inferior_errors);

			int target_int_size, target_long_size, target_address_size;
			CommandError result = mono_debugger_server_get_target_info
				(server_handle, out target_int_size, out target_long_size,
				 out target_address_size);
			handle_error (result);

			target_info = new TargetInfo (target_int_size, target_long_size,
						      target_address_size);
		}

		void read_mono_debugger_info ()
		{
			ITargetLocation symbol_info = bfd_symtab ["MONO_DEBUGGER__debugger_info"];
			if (symbol_info != null) {
				ITargetMemoryReader header = ReadMemory (symbol_info, 16);
				if (header.ReadLongInteger () != OffsetTable.Magic)
					throw new SymbolTableException ();
				if (header.ReadInteger () != OffsetTable.Version)
					throw new SymbolTableException ();

				int size = (int) header.ReadInteger ();

				ITargetMemoryReader table = ReadMemory (symbol_info, size);
				mono_debugger_info = new MonoDebuggerInfo (table);
				Console.WriteLine ("MONO DEBUGGER INFO: {0}", mono_debugger_info);
			}
		}

		void child_exited ()
		{
			child_pid = 0;
			if (ChildExited != null)
				ChildExited ();
		}

		void child_message (ChildMessage message, int arg)
		{
			if (ChildMessage != null)
				ChildMessage (message, arg);
		}

		void inferior_output (string line)
		{
			if (TargetOutput != null)
				TargetOutput (line);
		}

		void inferior_errors (string line)
		{
			if (TargetError != null)
				TargetError (line);
		}

		//
		// ITargetInfo
		//

		public int TargetIntegerSize {
			get {
				return target_info.TargetIntegerSize;
			}
		}

		public int TargetLongIntegerSize {
			get {
				return target_info.TargetLongIntegerSize;
			}
		}

		public int TargetAddressSize {
			get {
				return target_info.TargetAddressSize;
			}
		}

		//
		// ITargetMemoryAccess
		//

		public byte ReadByte (ITargetLocation location)
		{
			IntPtr data;
			CommandError result = mono_debugger_server_read_memory (
				server_handle, location.Location, 1, out data);
			handle_error (result);

			byte retval = Marshal.ReadByte (data);
			g_free (data);
			return retval;
		}

		public int ReadInteger (ITargetLocation location)
		{
			IntPtr data;
			CommandError result = mono_debugger_server_read_memory (
				server_handle, location.Location, sizeof (int), out data);
			handle_error (result);

			int retval = Marshal.ReadInt32 (data);
			g_free (data);
			return retval;
		}

		public long ReadLongInteger (ITargetLocation location)
		{
			IntPtr data;
			CommandError result = mono_debugger_server_read_memory (
				server_handle, location.Location, sizeof (long), out data);
			handle_error (result);

			long retval = Marshal.ReadInt64 (data);
			g_free (data);
			return retval;
		}

		public ITargetLocation ReadAddress (ITargetLocation location)
		{
			switch (target_info.TargetAddressSize) {
			case 4:
				return new TargetLocation (ITargetMemoryAccess.ReadInteger (location));

			case 8:
				return new TargetLocation (ITargetMemoryAccess.ReadLongInteger (location));

			default:
				throw new TargetMemoryException (
					"Unknown target address size " + target_info.TargetAddressSize);
			}
		}

		public string ReadString (ITargetLocation location)
		{
			throw new TargetMemoryException (location);
		}

		public ITargetMemoryReader ReadMemory (ITargetLocation location, int size)
		{
			IntPtr data;
			CommandError result = mono_debugger_server_read_memory (
				server_handle, location.Location, size, out data);
			handle_error (result);

			byte[] retval = new byte [size];
			Marshal.Copy (data, retval, 0, size);
			g_free (data);

			return new TargetReader (retval, target_info);
		}

		//
		// IInferior
		//

		public event TargetOutputHandler TargetOutput;
		public event TargetOutputHandler TargetError;
		public event StateChangedHandler StateChanged;

		TargetState target_state = TargetState.NO_TARGET;
		public TargetState State {
			get {
				return target_state;
			}
		}

		void change_target_state (TargetState new_state)
		{
			if (new_state == target_state)
				return;

			target_state = new_state;

			if (StateChanged != null)
				StateChanged (target_state);
		}

		public void Continue ()
		{
			send_command (ServerCommand.CONTINUE);
		}

		public void Detach ()
		{
			send_command (ServerCommand.DETACH);
		}

		public void Shutdown ()
		{
			send_command (ServerCommand.SHUTDOWN);
		}

		public void Kill ()
		{
			send_command (ServerCommand.KILL);
		}

		public void Step ()
		{
			send_command (ServerCommand.STEP);
		}

		public void Next ()
		{
			throw new NotImplementedException ();
		}

		public ITargetLocation Frame ()
		{
			send_command (ServerCommand.GET_PC);
			return new TargetLocation (read_long ());
		}

		//
		// IDisposable
		//

		private bool disposed = false;

		protected virtual void Dispose (bool disposing)
		{
			// Check to see if Dispose has already been called.
			if (!this.disposed) {
				// If this is a call to Dispose,
				// dispose all managed resources.
				if (disposing) {
					// Do stuff here
				}
				
				// Release unmanaged resources
				this.disposed = true;

				lock (this) {
					if (child_pid != 0) {
						mono_debugger_glue_kill_process (child_pid, false);
						child_pid = 0;
					}
				}
			}
		}

		public void Dispose ()
		{
			Dispose (true);
			// Take yourself off the Finalization queue
			GC.SuppressFinalize (this);
		}

		~Inferior ()
		{
			Dispose (false);
		}
	}
}

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Configuration;
using System.Globalization;
using System.Reflection;
using System.Collections;
using System.Collections.Specialized;
using System.Runtime.InteropServices;

using Mono.Debugger.Backends;
using Mono.Debugger.Languages;
using Mono.Debugger.Languages.CSharp;
using Mono.Debugger.Architecture;

namespace Mono.Debugger
{
	public delegate void ThreadEventHandler (ThreadManager manager, Process process);

	public class ThreadManager
	{
		internal ThreadManager (DebuggerBackend backend)
		{
			this.backend = backend;
			this.SymbolTableManager = backend.SymbolTableManager;

			breakpoint_manager = new BreakpointManager ();

			thread_hash = Hashtable.Synchronized (new Hashtable ());
			
			global_group = ThreadGroup.CreateThreadGroup ("global");
			thread_lock_mutex = new Mutex ();
			address_domain = new AddressDomain ("global");

			start_event = new ManualResetEvent (false);
			completed_event = new AutoResetEvent (false);
			command_mutex = new Mutex ();

			ready_event = new ManualResetEvent (false);
			engine_event = Semaphore.CreateThreadManagerSemaphore ();
			wait_event = new AutoResetEvent (false);
		}

		SingleSteppingEngine the_engine;
		internal readonly SymbolTableManager SymbolTableManager;

		ProcessStart start;
		DebuggerBackend backend;
		BreakpointManager breakpoint_manager;
		Thread inferior_thread;
		Thread wait_thread;
		ManualResetEvent ready_event;
		AutoResetEvent wait_event;
		Semaphore engine_event;
		Hashtable thread_hash;

		int thread_lock_level;
		Mutex thread_lock_mutex;
		AddressDomain address_domain;
		ThreadGroup global_group;

		Process main_process;

		ManualResetEvent start_event;
		AutoResetEvent completed_event;
		Mutex command_mutex;
		bool sync_command_running;
		bool abort_requested;

		[DllImport("monodebuggerserver")]
		static extern int mono_debugger_server_global_wait (out long status);

		[DllImport("monodebuggerserver")]
		static extern int mono_debugger_server_get_pending_sigint ();

		void start_inferior ()
		{
			the_engine = new SingleSteppingEngine (this, start);

			Report.Debug (DebugFlags.Threads, "Thread manager started: {0}",
				      the_engine.PID);

			thread_hash.Add (the_engine.PID, the_engine);

			OnThreadCreatedEvent (the_engine.Process);

			wait_event.Set ();

			while (!abort_requested) {
				engine_thread_main ();
			}
		}

		bool engine_is_ready = false;
		Exception start_error = null;

		// <remarks>
		//   These three variables are shared between the two threads, so you need to
		//   lock (this) before accessing/modifying them.
		// </remarks>
		Command current_command = null;
		CommandResult command_result = null;
		SingleSteppingEngine command_engine = null;
		SingleSteppingEngine current_event = null;
		long current_event_status = 0;

		void engine_error (Exception ex)
		{
			lock (this) {
				start_error = ex;
				start_event.Set ();
			}
		}

		// <remarks>
		//   This is only called on startup and blocks until the background thread
		//   has actually been started and it's waiting for commands.
		// </summary>
		void wait_until_engine_is_ready ()
		{
			while (!start_event.WaitOne ())
				;

			if (start_error != null)
				throw start_error;
		}

		public Process StartApplication (ProcessStart start)
		{
			this.start = start;

			wait_thread = new Thread (new ThreadStart (start_wait_thread));
			wait_thread.Start ();

			inferior_thread = new Thread (new ThreadStart (start_inferior));
			inferior_thread.Start ();

			ready_event.WaitOne ();

			OnInitializedEvent (main_process);
			OnMainThreadCreatedEvent (main_process);
			return main_process;
		}

		bool initialized;
		MonoThreadManager mono_manager;
		TargetAddress main_method = TargetAddress.Null;

		internal void Initialize (Inferior inferior)
		{
			if (inferior.CurrentFrame != main_method)
				throw new InternalError ("Target stopped unexpectedly at {0}, " +
							 "but main is at {1}", inferior.CurrentFrame, main_method);

			backend.ReachedMain ();
			inferior.UpdateModules ();
		}

		internal void ReachedMain ()
		{
			ready_event.Set ();
		}

		public Process[] Threads {
			get {
				lock (this) {
					Process[] procs = new Process [thread_hash.Count];
					int i = 0;
					foreach (SingleSteppingEngine engine in thread_hash.Values)
						procs [i] = engine.Process;
					return procs;
				}
			}
		}

		// <summary>
		//   Stop all currently running threads without sending any notifications.
		//   The threads are automatically resumed to their previos state when
		//   ReleaseGlobalThreadLock() is called.
		// </summary>
		internal void AcquireGlobalThreadLock (SingleSteppingEngine caller)
		{
			thread_lock_mutex.WaitOne ();
			Report.Debug (DebugFlags.Threads,
				      "Acquiring global thread lock: {0} {1}",
				      caller, thread_lock_level);
			if (thread_lock_level++ > 0)
				return;
			foreach (SingleSteppingEngine engine in thread_hash.Values) {
				if (engine == caller)
					continue;
				engine.AcquireThreadLock ();
			}
			Report.Debug (DebugFlags.Threads,
				      "Done acquiring global thread lock: {0}",
				      caller);
		}

		internal void ReleaseGlobalThreadLock (SingleSteppingEngine caller)
		{
			Report.Debug (DebugFlags.Threads,
				      "Releasing global thread lock: {0} {1}",
				      caller, thread_lock_level);
			if (--thread_lock_level > 0) {
				thread_lock_mutex.ReleaseMutex ();
				return;
			}
				
			foreach (SingleSteppingEngine engine in thread_hash.Values) {
				if (engine == caller)
					continue;
				engine.ReleaseThreadLock ();
			}
			thread_lock_mutex.ReleaseMutex ();
			Report.Debug (DebugFlags.Threads,
				      "Released global thread lock: {0}", caller);
		}

		void thread_created (Inferior inferior, int pid)
		{
			Report.Debug (DebugFlags.Threads, "Thread created: {0}", pid);

			Inferior new_inferior = inferior.CreateThread ();

			SingleSteppingEngine new_thread = new SingleSteppingEngine (this, new_inferior, pid);

			thread_hash.Add (pid, new_thread);

			if ((mono_manager != null) &&
			    mono_manager.ThreadCreated (new_thread, new_inferior, inferior)) {
				main_process = new_thread.Process;

				main_method = mono_manager.Initialize (the_engine, inferior);

				Report.Debug (DebugFlags.Threads,
					      "Managed main address is {0}",
					      main_method);

				new_thread.Start (main_method, true);
			}

			new_inferior.Continue ();
			OnThreadCreatedEvent (new_thread.Process);

			inferior.Continue ();
		}

		internal bool HandleChildEvent (Inferior inferior, Inferior.ChildEvent cevent)
		{
			if (cevent.Type == Inferior.ChildEventType.NONE) {
				inferior.Continue ();
				return true;
			}

			if (!initialized) {
				if ((cevent.Type != Inferior.ChildEventType.CHILD_STOPPED) ||
				    (cevent.Argument != 0))
					throw new InternalError (
						"Received unexpected initial child event {0}",
						cevent);

				mono_manager = MonoThreadManager.Initialize (this, inferior);

				main_process = the_engine.Process;
				if (mono_manager == null)
					main_method = inferior.MainMethodAddress;
				else
					main_method = TargetAddress.Null;
				the_engine.Start (main_method, true);

				initialized = true;
				return true;
			}

			if (cevent.Type == Inferior.ChildEventType.CHILD_CREATED_THREAD) {
				thread_created (inferior, (int) cevent.Argument);

				return true;
			}

			return false;
		}

		public DebuggerBackend DebuggerBackend {
			get { return backend; }
		}

		internal BreakpointManager BreakpointManager {
			get { return breakpoint_manager; }
		}

		public Process MainProcess {
			get { return main_process; }
		}

		public event ThreadEventHandler InitializedEvent;
		public event ThreadEventHandler MainThreadCreatedEvent;
		public event ThreadEventHandler ThreadCreatedEvent;
		public event ThreadEventHandler ThreadExitedEvent;
		public event TargetExitedHandler TargetExitedEvent;

		public event TargetOutputHandler TargetOutputEvent;
		public event TargetOutputHandler TargetErrorOutputEvent;
		public event DebuggerOutputHandler DebuggerOutputEvent;
		public event DebuggerErrorHandler DebuggerErrorEvent;

		protected virtual void OnInitializedEvent (Process new_process)
		{
			if (InitializedEvent != null)
				InitializedEvent (this, new_process);
		}

		protected virtual void OnMainThreadCreatedEvent (Process new_process)
		{
			if (MainThreadCreatedEvent != null)
				MainThreadCreatedEvent (this, new_process);
		}

		protected virtual void OnThreadCreatedEvent (Process new_process)
		{
			if (ThreadCreatedEvent != null)
				ThreadCreatedEvent (this, new_process);
		}

		protected virtual void OnThreadExitedEvent (Process process)
		{
			if (ThreadExitedEvent != null)
				ThreadExitedEvent (this, process);
		}

		protected virtual void OnTargetExitedEvent ()
		{
			if (TargetExitedEvent != null)
				TargetExitedEvent ();
		}

		public void Kill ()
		{
			if (main_process != null)
				main_process.Kill ();
		}

		void inferior_output (bool is_stderr, string line)
		{
			if (TargetOutputEvent != null)
				TargetOutputEvent (is_stderr, line);
		}

		void debugger_output (string line)
		{
			if (DebuggerOutputEvent != null)
				DebuggerOutputEvent (line);
		}

		void debugger_error (object sender, string message, Exception e)
		{
			if (DebuggerErrorEvent != null)
				DebuggerErrorEvent (this, message, e);
		}

		// <summary>
		//   The 'command_mutex' is used to protect the engine's main loop.
		//
		//   Before sending any command to it, you must acquire the mutex
		//   and release it when you're done with the command.
		//
		//   Note that you must not keep this mutex when returning from the
		//   function which acquired it.
		// </summary>
		internal bool AcquireCommandMutex (SingleSteppingEngine engine)
		{
			if (!command_mutex.WaitOne (0, false))
				return false;

			command_engine = engine;
			return true;
		}

		internal void ReleaseCommandMutex ()
		{
			command_engine = null;
			command_mutex.ReleaseMutex ();
		}

		// <summary>
		//   Sends a synchronous command to the background thread and wait until
		//   it is completed.  This command never throws any exceptions, but returns
		//   an appropriate CommandResult if something went wrong.
		//
		//   This is used for non-steping commands such as getting a backtrace.
		// </summary>
		// <remarks>
		//   You must own either the 'command_mutex' or the `this' lock prior to
		//   calling this and you must make sure you aren't currently running any
		//   async operations.
		// </remarks>
		internal CommandResult SendSyncCommand (Command command)
		{
			if (Thread.CurrentThread == inferior_thread) {
				try {
					return command.Process.ProcessCommand (command);
				} catch (ThreadAbortException) {
					;
				} catch (Exception e) {
					return new CommandResult (e);
				}
			}

			if (!AcquireCommandMutex (null))
				return CommandResult.Busy;

			lock (this) {
				current_command = command;
				completed_event.Reset ();
				sync_command_running = true;
				engine_event.Set ();
			}

			completed_event.WaitOne ();

			CommandResult result;
			lock (this) {
				result = command_result;
				command_result = null;
				current_command = null;
			}

			command_mutex.ReleaseMutex ();
			if (result != null)
				return result;
			else
				return new CommandResult (CommandResultType.UnknownError, null);
		}

		// <summary>
		//   Sends an asynchronous command to the background thread.  This is used
		//   for all stepping commands, no matter whether the user requested a
		//   synchronous or asynchronous operation.
		// </summary>
		// <remarks>
		//   You must own the 'command_mutex' before calling this method and you must
		//   make sure you aren't currently running any async commands.
		// </remarks>
		internal void SendAsyncCommand (Command command)
		{
			lock (this) {
				current_command = command;
				engine_event.Set ();
			}
		}

		// <summary>
		//   The heart of the SingleSteppingEngine.  This runs in a background
		//   thread and processes stepping commands and events.
		//
		//   For each application we're debugging, there is just one SingleSteppingEngine,
		//   no matter how many threads the application has.  The engine is using one single
		//   event loop which is processing commands from the user and events from all of
		//   the application's threads.
		// </summary>
		void engine_thread_main ()
		{
			Report.Debug (DebugFlags.Wait, "ThreadManager waiting");

			engine_event.Wait ();

			Report.Debug (DebugFlags.Wait, "ThreadManager woke up");

			long status;
			SingleSteppingEngine event_engine;

			lock (this) {
				event_engine = current_event;
				status = current_event_status;
			}

			if (event_engine != null) {
				try {
					event_engine.ProcessEvent (status);
				} catch (ThreadAbortException) {
					;
				} catch (Exception e) {
					Console.WriteLine ("EXCEPTION: {0}", e);
				}

				lock (this) {
					current_event = null;
					current_event_status = 0;
					wait_event.Set ();
				}

				if (!engine_is_ready) {
					engine_is_ready = true;
					start_event.Set ();
				}
				return;
			}

			//
			// We caught a SIGINT.
			//
			if (mono_debugger_server_get_pending_sigint () > 0) {
				Report.Debug (DebugFlags.EventLoop,
					      "ThreadManager received SIGINT: {0} {1}",
					      command_engine, sync_command_running);

				lock (this) {
					if (sync_command_running) {
						command_result = CommandResult.Interrupted;
						current_command = null;
						sync_command_running = false;
						completed_event.Set ();
						return;
					}

					if (command_engine != null)
						command_engine.Interrupt ();
				}
				return;
			}

			if (abort_requested) {
				Report.Debug (DebugFlags.Wait, "Abort requested");
				return;
			}

			Command command;
			lock (this) {
				command = current_command;
				current_command = null;

				if (command == null)
					return;
			}

			if (command == null)
				return;

			Report.Debug (DebugFlags.EventLoop,
				      "ThreadManager received command: {0}", command);

			// These are synchronous commands; ie. the caller blocks on us
			// until we finished the command and sent the result.
			if (command.Type != CommandType.Operation) {
				CommandResult result;
				try {
					result = command.Process.ProcessCommand (command);
				} catch (ThreadAbortException) {
					;
					return;
				} catch (Exception e) {
					result = new CommandResult (e);
				}

				lock (this) {
					command_result = result;
					current_command = null;
					sync_command_running = false;
					completed_event.Set ();
				}
			} else {
				try {
					command.Process.ProcessCommand (command.Operation);
				} catch (ThreadAbortException) {
					return;
				} catch (Exception e) {
					Console.WriteLine ("EXCEPTION: {0} {1}", command, e);
				}
			}
		}

		void start_wait_thread ()
		{
			while (!abort_requested) {
				wait_thread_main ();
			}
		}

		void wait_thread_main ()
		{
			Report.Debug (DebugFlags.Wait, "Wait thread sleeping");
			wait_event.WaitOne ();

		again:
			Report.Debug (DebugFlags.Wait, "Wait thread waiting");

			//
			// Wait until we got an event from the target or a command from the user.
			//

			int pid;
			long status;
			pid = mono_debugger_server_global_wait (out status);

			//
			// Note: `pid' is basically just an unique number which identifies the
			//       SingleSteppingEngine of this event.
			//

			if (pid > 0) {
				Report.Debug (DebugFlags.Wait,
					      "ThreadManager received event: {0} {1:x}",
					      pid, status);

				SingleSteppingEngine event_engine = (SingleSteppingEngine) thread_hash [pid];
				if (event_engine == null)
					throw new InternalError ("Got event {0:x} for unknown pid {1}",
								 status, pid);

				lock (this) {
					if (current_event != null)
						throw new InternalError ();

					current_event = event_engine;
					current_event_status = status;
					engine_event.Set ();
				}
			}

			if (abort_requested) {
				Report.Debug (DebugFlags.Wait, "Abort requested");
				return;
			}
		}

		//
		// IDisposable
		//

		protected virtual void DoDispose ()
		{
			if (inferior_thread != null) {
				lock (this) {
					abort_requested = true;
					engine_event.Set ();
				}
				inferior_thread.Join ();
				wait_thread.Abort ();
			}

			SingleSteppingEngine[] threads = new SingleSteppingEngine [thread_hash.Count];
			thread_hash.Values.CopyTo (threads, 0);
			for (int i = 0; i < threads.Length; i++)
				threads [i].Dispose ();
		}

		private bool disposed = false;

		private void check_disposed ()
		{
			if (disposed)
				throw new ObjectDisposedException ("ThreadManager");
		}

		protected virtual void Dispose (bool disposing)
		{
			// Check to see if Dispose has already been called.
			lock (this) {
				if (disposed)
					return;

				disposed = true;
			}

			// If this is a call to Dispose, dispose all managed resources.
			if (disposing) {
				DoDispose ();
			}
		}

		public void Dispose ()
		{
			Dispose (true);
			// Take yourself off the Finalization queue
			GC.SuppressFinalize (this);
		}

		~ThreadManager ()
		{
			Dispose (false);
		}

		public AddressDomain AddressDomain {
			get { return address_domain; }
		}
	}
}

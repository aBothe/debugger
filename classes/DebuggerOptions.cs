using System;
using System.IO;
using System.Reflection;
using System.Configuration;
using System.Collections;
using System.Collections.Specialized;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Xml;
using System.Xml.XPath;

using Mono.Debugger.Languages;
using Mono.Debugger.Backends;

namespace Mono.Debugger
{
	public class DebuggerOptions : DebuggerMarshalByRefObject
	{
		static int next_id = 0;
		public readonly int ID = ++next_id;

		string file;
		string[] inferior_args;
		string jit_optimizations;
		string[] jit_arguments;
		string working_directory;
		bool is_script, start_target;
		bool has_debug_flags;
		DebugFlags debug_flags = DebugFlags.None;
		string debug_output;
		bool in_emacs;
		string mono_prefix, mono_path;

		/* The executable file we're debugging */
		public string File {
			get { return file; }
			set { file = value; }
		}

		/* argv[1...n] for the inferior process */
		public string[] InferiorArgs {
			get { return inferior_args; }
			set { inferior_args = value; }
		}

		/* JIT optimization flags affecting the inferior
		 * process */
		public string JitOptimizations {
			get { return jit_optimizations; }
			set { jit_optimizations = value; }
		}

		public string[] JitArguments {
			get { return jit_arguments; }
			set { jit_arguments = value; }
		}

		/* The inferior process's working directory */
		public string WorkingDirectory {
			get { return working_directory; }
			set { working_directory = value; }
		}

		/* true if we're running in a script */
		public bool IsScript {
			get { return is_script; }
			set { is_script = value; }
		}

		/* true if we want to start the application immediately */
		public bool StartTarget {
			get { return start_target; }
			set { start_target = value; }
		}
	  
		/* the value of the -debug-flags: command line argument */
		public DebugFlags DebugFlags {
			get { return debug_flags; }
			set {
				debug_flags = value;
				has_debug_flags = true;
			}
		}

		public bool HasDebugFlags {
			get { return has_debug_flags; }
		}

		public string DebugOutput {
			get { return debug_output; }
			set { debug_output = value; }
		}

		/* true if -f/-fullname is specified on the command line */
		public bool InEmacs {
			get { return in_emacs; }
			set { in_emacs = value; }
		}

		/* non-null if the user specified the -mono-prefix
		 * command line argument */
		public string MonoPrefix {
			get { return mono_prefix; }
			set { mono_prefix = value; }
		}

		/* non-null if the user specified the -mono command line argument */
		public string MonoPath {
			get { return mono_path; }
			set { mono_path = value; }
		}

		Hashtable user_environment;

		string[] clone (string[] array)
		{
			if (array == null)
				return null;
			string[] new_array = new string [array.Length];
			array.CopyTo (new_array, 0);
			return new_array;
		}

		Hashtable clone (Hashtable hash)
		{
			if (hash == null)
				return null;
			Hashtable new_hash = new Hashtable ();
			foreach (string key in hash.Keys)
				new_hash.Add (key, hash [key]);
			return new_hash;
		}

		public DebuggerOptions Clone ()
		{
			DebuggerOptions options = new DebuggerOptions ();
			options.file = file;
			options.inferior_args = clone (inferior_args);
			options.jit_optimizations = jit_optimizations;
			options.jit_arguments = clone (jit_arguments);
			options.working_directory = working_directory;
			options.is_script = is_script;
			options.start_target = start_target;
			options.debug_flags = debug_flags;
			options.has_debug_flags = has_debug_flags;
			options.debug_output = debug_output;
			options.in_emacs = in_emacs;
			options.mono_prefix = mono_prefix;
			options.mono_path = mono_path;
			options.user_environment = clone (user_environment);
			return options;
		}

		public Hashtable UserEnvironment {
			get { return user_environment; }
		}

		public void SetEnvironment (string name, string value)
		{
			if (user_environment == null)
				user_environment = new Hashtable ();

			if (user_environment.Contains (name)) {
				if (value == null)
					user_environment.Remove (name);
				else
					user_environment [name] = value;
			} else if (value != null)
				user_environment.Add (name, value);
		}

		internal void GetSessionData (XmlElement root)
		{
			XmlElement file_e = root.OwnerDocument.CreateElement ("File");
			file_e.InnerText = file;
			root.AppendChild (file_e);

			if (InferiorArgs != null) {
				foreach (string arg in InferiorArgs) {
					XmlElement arg_e = root.OwnerDocument.CreateElement ("InferiorArgs");
					arg_e.InnerText = arg;
					root.AppendChild (arg_e);
				}
			}

			if (JitArguments != null) {
				foreach (string arg in JitArguments) {
					XmlElement arg_e = root.OwnerDocument.CreateElement ("JitArguments");
					arg_e.InnerText = arg;
					root.AppendChild (arg_e);
				}
			}

			if (JitOptimizations != null) {
				XmlElement opt_e = root.OwnerDocument.CreateElement ("JitOptimizations");
				opt_e.InnerText = JitOptimizations;
				root.AppendChild (opt_e);
			}
			if (WorkingDirectory != null) {
				XmlElement cwd_e = root.OwnerDocument.CreateElement ("WorkingDirectory");
				cwd_e.InnerText = WorkingDirectory;
				root.AppendChild (cwd_e);
			}
			if (MonoPrefix != null) {
				XmlElement prefix_e = root.OwnerDocument.CreateElement ("MonoPrefix");
				prefix_e.InnerText = MonoPrefix;
				root.AppendChild (prefix_e);
			}
			if (MonoPath != null) {
				XmlElement path_e = root.OwnerDocument.CreateElement ("MonoPath");
				path_e.InnerText = MonoPath;
				root.AppendChild (path_e);
			}
		}

		private DebuggerOptions ()
		{ }

		void append_array (ref string[] array, string value)
		{
			if (array == null) {
				array = new string [1];
				array [0] = value;
			} else {
				string[] new_array = new string [array.Length + 1];
				array.CopyTo (new_array, 0);
				new_array [array.Length] = value;
				array = new_array;
			}
		}

		internal DebuggerOptions (XPathNodeIterator iter)
		{
			while (iter.MoveNext ()) {
				switch (iter.Current.Name) {
				case "File":
					file = iter.Current.Value;
					break;
				case "InferiorArgs":
					append_array (ref inferior_args, iter.Current.Value);
					break;
				case "JitArguments":
					append_array (ref jit_arguments, iter.Current.Value);
					break;
				case "WorkingDirectory":
					working_directory = iter.Current.Value;
					break;
				case "MonoPrefix":
					mono_prefix = iter.Current.Value;
					break;
				case "MonoPath":
					mono_path = iter.Current.Value;
					break;
				default:
					throw new InternalError ();
				}
			}

			if (inferior_args == null)
				inferior_args = new string [0];
		}

		static void About ()
		{
			Console.WriteLine (
				"The Mono Debugger is (C) 2003-2006 Novell, Inc.\n\n" +
				"The debugger source code is released under the terms of the GNU GPL\n\n" +

				"For more information on Mono, visit the project Web site\n" +
				"   http://www.go-mono.com\n\n" +

				"The debugger was written by Martin Baulig and Chris Toshok");

			Environment.Exit (0);
		}

		static void Usage ()
		{
			Console.WriteLine (
				"Mono Debugger, (C) 2003-2006 Novell, Inc.\n" +
				"mdb [options] [exe-file]\n" +
				"mdb [options] -args exe-file [inferior-arguments ...]\n\n" +
				
				"   -args                     Arguments after exe-file are passed to inferior\n" +
				"   -debug-flags:PARAM        Sets the debugging flags\n" +
				"   -fullname                 Sets the debugging flags (short -f)\n" +
				"   -jit-optimizations:PARAM  Set jit optimizations used on the inferior process\n" +
				"   -mono:PATH                Override the inferior mono\n" +
				"   -mono-prefix:PATH         Override the mono prefix\n" +
				"   -native-symtabs           Load native symtabs\n" +
				"   -script                  \n" +
				"   -usage                   \n" +
				"   -version                  Display version and licensing information (short -V)\n" +
				"   -working-directory:DIR    Sets the working directory (short -cd)\n"
				);
		}

		public static DebuggerOptions ParseCommandLine (string[] args)
		{
			DebuggerOptions options = new DebuggerOptions ();
			int i;
			bool parsing_options = true;
			bool args_follow = false;

			for (i = 0; i < args.Length; i++) {
				string arg = args[i];

				if (arg == "")
					continue;

				if (!parsing_options)
					break;

				if (arg.StartsWith ("-")) {
					if (ParseOption (options, arg, ref args, ref i, ref args_follow))
						continue;
					Usage ();
					Console.WriteLine ("Unknown argument: {0}", arg);
					Environment.Exit (1);
				} else if (arg.StartsWith ("/")) {
					string unix_opt = "-" + arg.Substring (1);
					if (ParseOption (options, unix_opt, ref args, ref i, ref args_follow))
						continue;
				}

				options.File = arg;
				break;
			}

			if (args_follow) {
				string[] argv = new string [args.Length - i - 1];
				Array.Copy (args, i + 1, argv, 0, args.Length - i - 1);
				options.InferiorArgs = argv;
			} else {
				options.InferiorArgs = new string [0];
			}

			return options;
		}

		static string GetValue (ref string[] args, ref int i, string ms_val)
		{
			if (ms_val == "")
				return null;

			if (ms_val != null)
				return ms_val;

			if (i >= args.Length)
				return null;

			return args[++i];
		}

		static bool ParseDebugFlags (DebuggerOptions options, string value)
		{
			if (value == null)
				return false;

			int pos = value.IndexOf (':');
			if (pos > 0) {
				string filename = value.Substring (0, pos);
				value = value.Substring (pos + 1);
				
				options.DebugOutput = filename;
			}
			try {
				options.DebugFlags = (DebugFlags) Int32.Parse (value);
			} catch {
				return false;
			}
			return true;
		}

		static bool ParseOption (DebuggerOptions debug_options,
					 string option,
					 ref string [] args,
					 ref int i,
					 ref bool args_follow_exe)
		{
			int idx = option.IndexOf (':');
			string arg, value, ms_value = null;

			if (idx == -1){
				arg = option;
			} else {
				arg = option.Substring (0, idx);
				ms_value = option.Substring (idx + 1);
			}

			switch (arg) {
			case "-args":
				if (ms_value != null) {
					Usage ();
					Environment.Exit (1);
				}
				args_follow_exe = true;
				return true;

			case "-working-directory":
			case "-cd":
				value = GetValue (ref args, ref i, ms_value);
				if (value == null) {
					Usage ();
					Environment.Exit (1);
				}
				debug_options.WorkingDirectory = value;
				return true;

			case "-debug-flags":
				value = GetValue (ref args, ref i, ms_value);
				if (!ParseDebugFlags (debug_options, value)) {
					Usage ();
					Environment.Exit (1);
				}
				return true;

			case "-jit-optimizations":
				value = GetValue (ref args, ref i, ms_value);
				if (value == null) {
					Usage ();
					Environment.Exit (1);
				}
				debug_options.JitOptimizations = value;
				return true;

			case "-jit-arg":
				value = GetValue (ref args, ref i, ms_value);
				if (ms_value == null) {
					Usage ();
					Environment.Exit (1);
				}
				if (debug_options.JitArguments != null) {
					string[] old = debug_options.JitArguments;
					string[] new_args = new string [old.Length + 1];
					old.CopyTo (new_args, 0);
					new_args [old.Length] = value;
					debug_options.JitArguments = new_args;
				} else {
					debug_options.JitArguments = new string[] { value };
				}
				return true;

			case "-fullname":
			case "-f":
				if (ms_value != null) {
					Usage ();
					Environment.Exit (1);
				}
				debug_options.InEmacs = true;
				return true;

			case "-mono-prefix":
				value = GetValue (ref args, ref i, ms_value);
				if (value == null) {
					Usage ();
					Environment.Exit (1);
				}
				debug_options.MonoPrefix = value;
				return true;

			case "-mono":
				value = GetValue (ref args, ref i, ms_value);
				if (value == null) {
					Usage ();
					Environment.Exit (1);
				}
				debug_options.MonoPath = value;
				return true;

			case "-script":
				if (ms_value != null) {
					Usage ();
					Environment.Exit (1);
				}
				debug_options.IsScript = true;
				return true;

			case "-version":
			case "-V":
				if (ms_value != null) {
					Usage ();
					Environment.Exit (1);
				}
				About();
				Environment.Exit (1);
				return true;

			case "-help":
			case "--help":
			case "-h":
			case "-usage":
				if (ms_value != null) {
					Usage ();
					Environment.Exit (1);
				}
				Usage();
				Environment.Exit (1);
				return true;

			case "-run":
				if (ms_value != null) {
					Usage ();
					Environment.Exit (1);
				}
				debug_options.StartTarget = true;
				return true;
			}

			return false;
		}
	}
}
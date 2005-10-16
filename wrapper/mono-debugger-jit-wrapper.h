#ifndef __MONO_DEBUGGER_JIT_WRAPPER_H
#define __MONO_DEBUGGER_JIT_WRAPPER_H 1

#include <mono/metadata/mono-debug-debugger.h>

G_BEGIN_DECLS

typedef struct _MonoDebuggerInfo		MonoDebuggerInfo;
typedef struct _MonoDebuggerThread		MonoDebuggerThread;
typedef struct _MonoDebuggerManager             MonoDebuggerManager;

/*
 * There's a global data symbol called `MONO_DEBUGGER__debugger_info' which
 * contains pointers to global variables and functions which must be accessed
 * by the debugger.
 */
struct _MonoDebuggerInfo {
	guint64 magic;
	guint32 version;
	guint32 total_size;
	guint32 symbol_table_size;
	guint32 heap_size;
	guint8 ***mono_trampoline_code;
	MonoSymbolTable **symbol_table;
	guint64 (*compile_method) (guint64 method_argument);
	guint64 (*get_virtual_method) (guint64 object_argument, guint64 method_argument);
	guint64 (*get_boxed_object_method) (guint64 klass_argument, guint64 val_argument);
	guint64 (*insert_breakpoint) (guint64 method_argument, const gchar *string_argument);
	guint64 (*remove_breakpoint) (guint64 breakpoint);
	MonoInvokeFunc runtime_invoke;
	guint64 (*create_string) (guint64 dummy_argument, const gchar *string_argument);
	guint64 (*class_get_static_field_data) (guint64 klass);
	guint64 (*lookup_class) (guint64 image_argument, guint64 token_arg);
	guint64 (*lookup_type) (guint64 dummy_argument, const gchar *string_argument);
	guint64 (*lookup_assembly) (guint64 dummy_argument, const gchar *string_argument);
	guint64 (*run_finally) (guint64 argument1, guint64 argument2);
	gpointer heap;
};

/*
 * Thread structure.
 */
struct _MonoDebuggerThread {
	gpointer end_stack;
	guint32 tid;
	guint32 locked;
	gpointer func;
	gpointer start_stack;
};

struct _MonoDebuggerManager {
	guint32 size;
	guint32 thread_size;
	gpointer main_function;
	gpointer notification_address;
	MonoDebuggerThread *main_thread;
	guint32 main_tid;
};

enum {
	THREAD_MANAGER_ACQUIRE_GLOBAL_LOCK = 1,
	THREAD_MANAGER_RELEASE_GLOBAL_LOCK
};

enum {
	NOTIFICATION_INITIALIZE_MANAGED_CODE	= 1,
	NOTIFICATION_ADD_MODULE,
	NOTIFICATION_RELOAD_SYMTABS,
	NOTIFICATION_METHOD_COMPILED,
	NOTIFICATION_JIT_BREAKPOINT,
	NOTIFICATION_INITIALIZE_THREAD_MANAGER,
	NOTIFICATION_ACQUIRE_GLOBAL_THREAD_LOCK,
	NOTIFICATION_RELEASE_GLOBAL_THREAD_LOCK,
	NOTIFICATION_WRAPPER_MAIN,
	NOTIFICATION_MAIN_EXITED,
	NOTIFICATION_UNHANDLED_EXCEPTION,
	NOTIFICATION_THREAD_CREATED,
	NOTIFICATION_THREAD_ABORT,
	NOTIFICATION_THROW_EXCEPTION,
	NOTIFICATION_HANDLE_EXCEPTION
};

#define IO_LAYER(func) (* mono_debugger_io_layer.func)

int mono_debugger_main (MonoDomain *domain, const char *file, int argc, char **argv, char **envp);

void mono_debugger_thread_manager_init (void);
void mono_debugger_thread_manager_main (void);
void mono_debugger_thread_manager_add_thread (guint32 thread, gpointer stack_start, gpointer func);
void mono_debugger_thread_manager_thread_created (MonoDebuggerThread *thread);
void mono_debugger_thread_manager_start_resume (guint32 thread);
void mono_debugger_thread_manager_end_resume (guint32 thread);
void mono_debugger_thread_manager_acquire_global_thread_lock (void);
void mono_debugger_thread_manager_release_global_thread_lock (void);
void mono_debugger_init_icalls (void);

extern MonoDebuggerManager MONO_DEBUGGER__manager;

extern void (*mono_debugger_notification_function) (guint64 command, guint64 data, guint64 data2);
extern void mono_debugger_run_finally (void *start_ctx);

G_END_DECLS

#endif

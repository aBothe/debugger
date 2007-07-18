#include <server.h>
#include <breakpoints.h>
#include <sys/stat.h>
#include <sys/ptrace.h>
#include <sys/socket.h>
#include <sys/wait.h>
#include <signal.h>
#include <unistd.h>
#include <string.h>
#include <fcntl.h>
#include <errno.h>

#include "x86-linux-ptrace.h"
#include "x86-arch.h"

struct ArchInfo
{
	guint32 call_address;
	guint64 callback_argument;
	INFERIOR_REGS_TYPE current_regs;
	INFERIOR_FPREGS_TYPE current_fpregs;
	INFERIOR_REGS_TYPE *saved_regs;
	INFERIOR_FPREGS_TYPE *saved_fpregs;
	int saved_signal;
	GPtrArray *rti_stack;
	guint64 dr_control, dr_status;
	int dr_regs [DR_NADDR];
};

typedef struct
{
	INFERIOR_REGS_TYPE *saved_regs;
	INFERIOR_FPREGS_TYPE *saved_fpregs;
	int saved_signal;
	guint32 call_address;
	guint32 exc_address;
	gboolean debug;
	guint64 callback_argument;
} RuntimeInvokeData;

ArchInfo *
x86_arch_initialize (void)
{
	ArchInfo *arch = g_new0 (ArchInfo, 1);

	arch->rti_stack = g_ptr_array_new ();

	return arch;
}

void
x86_arch_finalize (ArchInfo *arch)
{
	g_ptr_array_free (arch->rti_stack, TRUE);
	g_free (arch->saved_regs);
     	g_free (arch->saved_fpregs);
	g_free (arch);
}

static ServerCommandError
server_ptrace_current_insn_is_bpt (ServerHandle *handle, guint32 *is_breakpoint)
{
	mono_debugger_breakpoint_manager_lock ();
	if (mono_debugger_breakpoint_manager_lookup (handle->bpm, INFERIOR_REG_EIP (handle->arch->current_regs)))
		*is_breakpoint = TRUE;
	else
		*is_breakpoint = FALSE;
	mono_debugger_breakpoint_manager_unlock ();

	return COMMAND_ERROR_NONE;
}

void
x86_arch_remove_breakpoints_from_target_memory (ServerHandle *handle, guint64 start,
						guint32 size, gpointer buffer)
{
	GPtrArray *breakpoints;
	guint8 *ptr = buffer;
	int i;

	mono_debugger_breakpoint_manager_lock ();

	breakpoints = mono_debugger_breakpoint_manager_get_breakpoints (handle->bpm);
	for (i = 0; i < breakpoints->len; i++) {
		BreakpointInfo *info = g_ptr_array_index (breakpoints, i);
		guint32 offset;

		if (info->is_hardware_bpt || !info->enabled)
			continue;
		if ((info->address < start) || (info->address >= start+size))
			continue;

		offset = (guint32) info->address - start;
		ptr [offset] = info->saved_insn;
	}

	mono_debugger_breakpoint_manager_unlock ();
}

static ServerCommandError
server_ptrace_get_frame (ServerHandle *handle, StackFrame *frame)
{
	ServerCommandError result;

	result = x86_arch_get_registers (handle);
	if (result != COMMAND_ERROR_NONE)
		return result;

	frame->address = (guint32) INFERIOR_REG_EIP (handle->arch->current_regs);
	frame->stack_pointer = (guint32) INFERIOR_REG_ESP (handle->arch->current_regs);
	frame->frame_address = (guint32) INFERIOR_REG_EBP (handle->arch->current_regs);
	return COMMAND_ERROR_NONE;
}

/*
 * This method is highly architecture and specific.
 * It will only work on the i386.
 */

static ServerCommandError
server_ptrace_call_method (ServerHandle *handle, guint64 method_address,
			   guint64 method_argument1, guint64 method_argument2,
			   guint64 callback_argument)
{
	ServerCommandError result = COMMAND_ERROR_NONE;
	ArchInfo *arch = handle->arch;
	guint32 new_esp, call_disp;

	guint8 code[] = { 0x68, 0x00, 0x00, 0x00, 0x00, 0x68, 0x00, 0x00,
			  0x00, 0x00, 0x68, 0x00, 0x00, 0x00, 0x00, 0x68,
			  0x00, 0x00, 0x00, 0x00, 0xe8, 0x00, 0x00, 0x00,
			  0x00, 0xcc };
	int size = sizeof (code);

	if (arch->saved_regs)
		return COMMAND_ERROR_RECURSIVE_CALL;

	new_esp = (guint32) INFERIOR_REG_ESP (arch->current_regs) - size;

	arch->saved_regs = g_memdup (&arch->current_regs, sizeof (arch->current_regs));
	arch->saved_fpregs = g_memdup (&arch->current_fpregs, sizeof (arch->current_fpregs));
	arch->call_address = new_esp + 26;
	arch->callback_argument = callback_argument;
	arch->saved_signal = handle->inferior->last_signal;
	handle->inferior->last_signal = 0;

	call_disp = (int) method_address - new_esp;

	*((guint32 *) (code+1)) = method_argument2 >> 32;
	*((guint32 *) (code+6)) = method_argument2 & 0xffffffff;
	*((guint32 *) (code+11)) = method_argument1 >> 32;
	*((guint32 *) (code+16)) = method_argument1 & 0xffffffff;
	*((guint32 *) (code+21)) = call_disp - 25;

	server_ptrace_write_memory (handle, (guint32) new_esp, size, code);
	if (result != COMMAND_ERROR_NONE)
		return result;

	INFERIOR_REG_ESP (arch->current_regs) = INFERIOR_REG_EIP (arch->current_regs) = new_esp;

	result = _server_ptrace_set_registers (handle->inferior, &arch->current_regs);
	if (result != COMMAND_ERROR_NONE)
		return result;

	return server_ptrace_continue (handle);
}

/*
 * This method is highly architecture and specific.
 * It will only work on the i386.
 */

static ServerCommandError
server_ptrace_call_method_1 (ServerHandle *handle, guint64 method_address,
			     guint64 method_argument, const gchar *string_argument,
			     guint64 callback_argument)
{
	ServerCommandError result = COMMAND_ERROR_NONE;
	ArchInfo *arch = handle->arch;
	guint32 new_esp, call_disp;

	static guint8 static_code[] = { 0x68, 0x00, 0x00, 0x00, 0x00, 0x68, 0x00, 0x00,
					0x00, 0x00, 0x68, 0x00, 0x00, 0x00, 0x00, 0xe8,
					0x00, 0x00, 0x00, 0x00, 0xcc };
	int static_size = sizeof (static_code);
	int size = static_size + strlen (string_argument) + 1;
	guint8 *code = g_malloc0 (size);
	memcpy (code, static_code, static_size);
	strcpy ((char *) (code + static_size), string_argument);

	if (arch->saved_regs)
		return COMMAND_ERROR_RECURSIVE_CALL;

	new_esp = (guint32) INFERIOR_REG_ESP (arch->current_regs) - size;

	arch->saved_regs = g_memdup (&arch->current_regs, sizeof (arch->current_regs));
	arch->saved_fpregs = g_memdup (&arch->current_fpregs, sizeof (arch->current_fpregs));
	arch->call_address = new_esp + 21;
	arch->callback_argument = callback_argument;
	arch->saved_signal = handle->inferior->last_signal;
	handle->inferior->last_signal = 0;

	call_disp = (int) method_address - new_esp;

	*((guint32 *) (code+1)) = new_esp + 21;
	*((guint32 *) (code+6)) = method_argument >> 32;
	*((guint32 *) (code+11)) = method_argument & 0xffffffff;
	*((guint32 *) (code+16)) = call_disp - 20;

	result = server_ptrace_write_memory (handle, (guint32) new_esp, size, code);
	if (result != COMMAND_ERROR_NONE)
		return result;

	INFERIOR_REG_ESP (arch->current_regs) = INFERIOR_REG_EIP (arch->current_regs) = new_esp;

	result = _server_ptrace_set_registers (handle->inferior, &arch->current_regs);
	if (result != COMMAND_ERROR_NONE)
		return result;

	return server_ptrace_continue (handle);
}

static ServerCommandError
server_ptrace_call_method_2 (ServerHandle *handle, guint64 method_address,
			     guint64 method_argument, guint64 callback_argument)
{
	ServerCommandError result = COMMAND_ERROR_NONE;
	ArchInfo *arch = handle->arch;
	RuntimeInvokeData *rdata;
	guint32 new_esp;

	int size = 57;
	guint8 *code = g_malloc0 (size);

	if (arch->saved_regs)
		return COMMAND_ERROR_RECURSIVE_CALL;

	new_esp = INFERIOR_REG_ESP (arch->current_regs) - size;

	*((guint32 *) code) = new_esp + size - 1;
	*((guint64 *) (code+4)) = new_esp + 20;
	*((guint64 *) (code+12)) = method_argument;
	*((guint32 *) (code+20)) = INFERIOR_REG_EAX (arch->current_regs);
	*((guint32 *) (code+24)) = INFERIOR_REG_EBX (arch->current_regs);
	*((guint32 *) (code+28)) = INFERIOR_REG_ECX (arch->current_regs);
	*((guint32 *) (code+32)) = INFERIOR_REG_EDX (arch->current_regs);
	*((guint32 *) (code+36)) = INFERIOR_REG_EBP (arch->current_regs);
	*((guint32 *) (code+40)) = INFERIOR_REG_ESP (arch->current_regs);
	*((guint32 *) (code+44)) = INFERIOR_REG_ESI (arch->current_regs);
	*((guint32 *) (code+48)) = INFERIOR_REG_EDI (arch->current_regs);
	*((guint32 *) (code+52)) = INFERIOR_REG_EIP (arch->current_regs);
	*((guint8 *) (code+56)) = 0xcc;

	rdata = g_new0 (RuntimeInvokeData, 1);
	rdata->saved_regs = g_memdup (&arch->current_regs, sizeof (arch->current_regs));
	rdata->saved_fpregs = g_memdup (&arch->current_fpregs, sizeof (arch->current_fpregs));
	rdata->call_address = new_esp + size;
	rdata->exc_address = 0;
	rdata->callback_argument = callback_argument;
	rdata->saved_signal = handle->inferior->last_signal;
	handle->inferior->last_signal = 0;

	server_ptrace_write_memory (handle, (guint32) new_esp, size, code);
	g_free (code);
	if (result != COMMAND_ERROR_NONE)
		return result;

	INFERIOR_REG_EIP (arch->current_regs) = method_address;
	INFERIOR_REG_ESP (arch->current_regs) = new_esp;

	g_ptr_array_add (arch->rti_stack, rdata);

	result = _server_ptrace_set_registers (handle->inferior, &arch->current_regs);
	if (result != COMMAND_ERROR_NONE)
		return result;

	return server_ptrace_continue (handle);
}

static ServerCommandError
server_ptrace_call_method_invoke (ServerHandle *handle, guint64 invoke_method,
				  guint64 method_argument, guint32 num_params,
				  guint32 blob_size, guint64 *param_data,
				  gint32 *offset_data, gconstpointer blob_data,
				  guint64 callback_argument, gboolean debug)
{
	ServerCommandError result = COMMAND_ERROR_NONE;
	ArchInfo *arch = handle->arch;
	RuntimeInvokeData *rdata;
	guint32 new_esp;
	int i;

	static guint8 static_code[] = { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
					0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
					0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
					0xcc };
	int static_size = sizeof (static_code);
	int size = static_size + (num_params + 3) * 4 + blob_size;
	guint8 *code = g_malloc0 (size);
	guint32 *ptr = (guint32 *) (code + static_size + blob_size);
	guint64 blob_start;

	memcpy (code, static_code, static_size);
	memcpy (code + static_size, blob_data, blob_size);

	if (arch->saved_regs)
		return COMMAND_ERROR_RECURSIVE_CALL;

	new_esp = (guint32) INFERIOR_REG_ESP (arch->current_regs) - size;
	blob_start = new_esp + static_size;

	for (i = 0; i < num_params; i++) {
		if (offset_data [i] >= 0)
			ptr [i] = blob_start + offset_data [i];
		else
			ptr [i] = param_data [i];
	}

	*((guint32 *) code) = new_esp + static_size - 1;
	*((guint32 *) (code+4)) = method_argument;
	*((guint32 *) (code+8)) = ptr [0];
	*((guint32 *) (code+12)) = new_esp + static_size + blob_size + 4;
	*((guint32 *) (code+16)) = new_esp + 20;

	rdata = g_new0 (RuntimeInvokeData, 1);
	rdata->saved_regs = g_memdup (&arch->current_regs, sizeof (arch->current_regs));
	rdata->saved_fpregs = g_memdup (&arch->current_fpregs, sizeof (arch->current_fpregs));
	rdata->call_address = new_esp + static_size;
	rdata->exc_address = new_esp + 20;
	rdata->callback_argument = callback_argument;
	rdata->debug = debug;
	rdata->saved_signal = handle->inferior->last_signal;
	handle->inferior->last_signal = 0;

	result = server_ptrace_write_memory (handle, (guint32) new_esp, size, code);
	g_free (code);
	if (result != COMMAND_ERROR_NONE)
		return result;

	INFERIOR_REG_EIP (arch->current_regs) = invoke_method;
	INFERIOR_REG_ESP (arch->current_regs) = new_esp;

	g_ptr_array_add (arch->rti_stack, rdata);

	result = _server_ptrace_set_registers (handle->inferior, &arch->current_regs);
	if (result != COMMAND_ERROR_NONE)
		return result;

	return server_ptrace_continue (handle);
}

static gboolean
check_breakpoint (ServerHandle *handle, guint64 address, guint64 *retval)
{
	BreakpointInfo *info;

	mono_debugger_breakpoint_manager_lock ();
	info = (BreakpointInfo *) mono_debugger_breakpoint_manager_lookup (handle->bpm, address);
	if (!info || !info->enabled) {
		mono_debugger_breakpoint_manager_unlock ();
		return FALSE;
	}

	*retval = info->id;
	mono_debugger_breakpoint_manager_unlock ();
	return TRUE;
}

static RuntimeInvokeData *
get_runtime_invoke_data (ArchInfo *arch)
{
	if (!arch->rti_stack->len)
		return NULL;

	return g_ptr_array_index (arch->rti_stack, arch->rti_stack->len - 1);
}

static ServerCommandError
x86_arch_get_registers (ServerHandle *handle)
{
	ServerCommandError result;

	result = _server_ptrace_get_registers (handle->inferior, &handle->arch->current_regs);
	if (result != COMMAND_ERROR_NONE)
		return result;

	_server_ptrace_get_fp_registers (handle->inferior, &handle->arch->current_fpregs);
	if (result != COMMAND_ERROR_NONE)
		return result;

	_server_ptrace_get_dr (handle->inferior, DR_STATUS, &handle->arch->dr_status);
	if (result != COMMAND_ERROR_NONE)
		return result;

	return COMMAND_ERROR_NONE;
}

ChildStoppedAction
x86_arch_child_stopped (ServerHandle *handle, int stopsig,
			guint64 *callback_arg, guint64 *retval, guint64 *retval2)
{
	ArchInfo *arch = handle->arch;
	InferiorHandle *inferior = handle->inferior;
	RuntimeInvokeData *rdata;
	int i;

	x86_arch_get_registers (handle);

	if (stopsig == SIGSTOP)
		return STOP_ACTION_INTERRUPTED;

	if ((guint32) INFERIOR_REG_EIP (arch->current_regs) - 1 == inferior->notification_address) {
		guint32 addr = (guint32) INFERIOR_REG_ESP (arch->current_regs) + 4;
		guint64 data [3];

		if (server_ptrace_read_memory (handle, addr, 24, &data))
			return STOP_ACTION_STOPPED;

		*callback_arg = data [0];
		*retval = data [1];
		*retval2 = data [2];

		return STOP_ACTION_NOTIFICATION;
	}

	for (i = 0; i < DR_NADDR; i++) {
		if (X86_DR_WATCH_HIT (arch, i)) {
			_server_ptrace_set_dr (inferior, DR_STATUS, 0);
			arch->dr_status = 0;
			*retval = arch->dr_regs [i];
			return STOP_ACTION_BREAKPOINT_HIT;
		}
	}

	if (check_breakpoint (handle, (guint32) INFERIOR_REG_EIP (arch->current_regs) - 1, retval)) {
		INFERIOR_REG_EIP (arch->current_regs)--;
		_server_ptrace_set_registers (inferior, &arch->current_regs);
		return STOP_ACTION_BREAKPOINT_HIT;
	}

	rdata = get_runtime_invoke_data (arch);
	if (rdata && (rdata->call_address == INFERIOR_REG_EIP (arch->current_regs))) {
		guint64 exc_object;

		if (_server_ptrace_set_registers (inferior, rdata->saved_regs) != COMMAND_ERROR_NONE)
			g_error (G_STRLOC ": Can't restore registers after returning from a call");

		if (_server_ptrace_set_fp_registers (inferior, rdata->saved_fpregs) != COMMAND_ERROR_NONE)
			g_error (G_STRLOC ": Can't restore FP registers after returning from a call");

		*callback_arg = rdata->callback_argument;
		*retval = (guint32) INFERIOR_REG_EAX (arch->current_regs);

		if (rdata->exc_address &&
		    (server_ptrace_peek_word (handle, rdata->exc_address, &exc_object) != COMMAND_ERROR_NONE))
			g_error (G_STRLOC ": Can't get exc object");

		*retval2 = (guint32) exc_object;

		inferior->last_signal = rdata->saved_signal;
		g_free (rdata->saved_regs);
		g_free (rdata->saved_fpregs);
		g_ptr_array_remove (arch->rti_stack, rdata);

		x86_arch_get_registers (handle);

		if (rdata->debug) {
			*retval = 0;
			g_free (rdata);
			return STOP_ACTION_CALLBACK_COMPLETED;
		}

		g_free (rdata);
		return STOP_ACTION_CALLBACK;
	}

	if (!arch->call_address || arch->call_address != INFERIOR_REG_EIP (arch->current_regs)) {
		guint64 code;

#if defined(__linux__) || defined(__FreeBSD__)
		if (stopsig != SIGTRAP)
			return STOP_ACTION_STOPPED;
#endif

		if (server_ptrace_peek_word (handle, GPOINTER_TO_SIZE (INFERIOR_REG_EIP (arch->current_regs) - 1), &code) != COMMAND_ERROR_NONE)
			return STOP_ACTION_STOPPED;

		if ((code & 0xff) == 0xcc) {
			*retval = 0;
			return STOP_ACTION_BREAKPOINT_HIT;
		}

		return STOP_ACTION_STOPPED;
	}

	if (_server_ptrace_set_registers (inferior, arch->saved_regs) != COMMAND_ERROR_NONE)
		g_error (G_STRLOC ": Can't restore registers after returning from a call");

	if (_server_ptrace_set_fp_registers (inferior, arch->saved_fpregs) != COMMAND_ERROR_NONE)
		g_error (G_STRLOC ": Can't restore FP registers after returning from a call");

	*callback_arg = arch->callback_argument;
	*retval = (guint32) INFERIOR_REG_EAX (arch->current_regs);
	*retval2 = (guint32) INFERIOR_REG_EDX (arch->current_regs);

	inferior->last_signal = arch->saved_signal;
	g_free (arch->saved_regs);
	g_free (arch->saved_fpregs);

	arch->saved_regs = NULL;
	arch->saved_fpregs = NULL;
	arch->saved_signal = 0;
	arch->call_address = 0;
	arch->callback_argument = 0;

	x86_arch_get_registers (handle);

	return STOP_ACTION_CALLBACK;
}

static ServerCommandError
server_ptrace_get_target_info (guint32 *target_int_size, guint32 *target_long_size,
			       guint32 *target_address_size, guint32 *is_bigendian)
{
	*target_int_size = sizeof (guint32);
	*target_long_size = sizeof (guint32);
	*target_address_size = sizeof (void *);
	*is_bigendian = 0;

	return COMMAND_ERROR_NONE;
}

static ServerCommandError
server_ptrace_get_registers (ServerHandle *handle, guint64 *values)
{
	ArchInfo *arch = handle->arch;

	values [DEBUGGER_REG_EBX] = (guint32) INFERIOR_REG_EBX (arch->current_regs);
	values [DEBUGGER_REG_ECX] = (guint32) INFERIOR_REG_ECX (arch->current_regs);
	values [DEBUGGER_REG_EDX] = (guint32) INFERIOR_REG_EDX (arch->current_regs);
	values [DEBUGGER_REG_ESI] = (guint32) INFERIOR_REG_ESI (arch->current_regs);
	values [DEBUGGER_REG_EDI] = (guint32) INFERIOR_REG_EDI (arch->current_regs);
	values [DEBUGGER_REG_EBP] = (guint32) INFERIOR_REG_EBP (arch->current_regs);
	values [DEBUGGER_REG_EAX] = (guint32) INFERIOR_REG_EAX (arch->current_regs);
	values [DEBUGGER_REG_DS] = (guint32) INFERIOR_REG_DS (arch->current_regs);
	values [DEBUGGER_REG_ES] = (guint32) INFERIOR_REG_ES (arch->current_regs);
	values [DEBUGGER_REG_FS] = (guint32) INFERIOR_REG_FS (arch->current_regs);
	values [DEBUGGER_REG_GS] = (guint32) INFERIOR_REG_GS (arch->current_regs);
	values [DEBUGGER_REG_EIP] = (guint32) INFERIOR_REG_EIP (arch->current_regs);
	values [DEBUGGER_REG_CS] = (guint32) INFERIOR_REG_CS (arch->current_regs);
	values [DEBUGGER_REG_EFLAGS] = (guint32) INFERIOR_REG_EFLAGS (arch->current_regs);
	values [DEBUGGER_REG_ESP] = (guint32) INFERIOR_REG_ESP (arch->current_regs);
	values [DEBUGGER_REG_SS] = (guint32) INFERIOR_REG_SS (arch->current_regs);

	return COMMAND_ERROR_NONE;
}

static ServerCommandError
server_ptrace_set_registers (ServerHandle *handle, guint64 *values)
{
	ArchInfo *arch = handle->arch;

	INFERIOR_REG_EBX (arch->current_regs) = values [DEBUGGER_REG_EBX];
	INFERIOR_REG_ECX (arch->current_regs) = values [DEBUGGER_REG_ECX];
	INFERIOR_REG_EDX (arch->current_regs) = values [DEBUGGER_REG_EDX];
	INFERIOR_REG_ESI (arch->current_regs) = values [DEBUGGER_REG_ESI];
	INFERIOR_REG_EDI (arch->current_regs) = values [DEBUGGER_REG_EDI];
	INFERIOR_REG_EBP (arch->current_regs) = values [DEBUGGER_REG_EBP];
	INFERIOR_REG_EAX (arch->current_regs) = values [DEBUGGER_REG_EAX];
	INFERIOR_REG_DS (arch->current_regs) = values [DEBUGGER_REG_DS];
	INFERIOR_REG_ES (arch->current_regs) = values [DEBUGGER_REG_ES];
	INFERIOR_REG_FS (arch->current_regs) = values [DEBUGGER_REG_FS];
	INFERIOR_REG_GS (arch->current_regs) = values [DEBUGGER_REG_GS];
	INFERIOR_REG_EIP (arch->current_regs) = values [DEBUGGER_REG_EIP];
	INFERIOR_REG_CS (arch->current_regs) = values [DEBUGGER_REG_CS];
	INFERIOR_REG_EFLAGS (arch->current_regs) = values [DEBUGGER_REG_EFLAGS];
	INFERIOR_REG_ESP (arch->current_regs) = values [DEBUGGER_REG_ESP];
	INFERIOR_REG_SS (arch->current_regs) = values [DEBUGGER_REG_SS];

	return _server_ptrace_set_registers (handle->inferior, &arch->current_regs);
}

static ServerCommandError
server_ptrace_push_registers (ServerHandle *handle, guint64 *new_esp)
{
	ArchInfo *arch = handle->arch;
	ServerCommandError result;

	INFERIOR_REG_ESP (arch->current_regs) -= sizeof (arch->current_regs);
	result = _server_ptrace_set_registers (handle->inferior, &arch->current_regs);
	if (result != COMMAND_ERROR_NONE)
		return result;

	*new_esp = INFERIOR_REG_ESP (arch->current_regs);

	result = server_ptrace_write_memory (
		handle, *new_esp, sizeof (arch->current_regs), &arch->current_regs);
	if (result != COMMAND_ERROR_NONE)
		return result;

	return COMMAND_ERROR_NONE;
}

static ServerCommandError
server_ptrace_pop_registers (ServerHandle *handle)
{
	ArchInfo *arch = handle->arch;
	ServerCommandError result;

	INFERIOR_REG_ESP (arch->current_regs) += sizeof (arch->current_regs);
	result = _server_ptrace_set_registers (handle->inferior, &arch->current_regs);
	if (result != COMMAND_ERROR_NONE)
		return result;

	return COMMAND_ERROR_NONE;
}

static ServerCommandError
do_enable (ServerHandle *handle, BreakpointInfo *breakpoint)
{
	ServerCommandError result;
	ArchInfo *arch = handle->arch;
	InferiorHandle *inferior = handle->inferior;
	char bopcode = 0xcc;
	guint32 address;

	if (breakpoint->enabled)
		return COMMAND_ERROR_NONE;

	address = (guint32) breakpoint->address;

	if (breakpoint->dr_index >= 0) {
		X86_DR_SET_RW_LEN (arch, breakpoint->dr_index, DR_RW_EXECUTE | DR_LEN_1);
		X86_DR_LOCAL_ENABLE (arch, breakpoint->dr_index);

		result = _server_ptrace_set_dr (inferior, breakpoint->dr_index, address);
		if (result != COMMAND_ERROR_NONE) {
			g_warning (G_STRLOC);
			return result;
		}

		result = _server_ptrace_set_dr (inferior, DR_CONTROL, arch->dr_control);
		if (result != COMMAND_ERROR_NONE) {
			g_warning (G_STRLOC);
			return result;
		}

		arch->dr_regs [breakpoint->dr_index] = breakpoint->id;
	} else {
		result = server_ptrace_read_memory (handle, address, 1, &breakpoint->saved_insn);
		if (result != COMMAND_ERROR_NONE)
			return result;

		result = server_ptrace_write_memory (handle, address, 1, &bopcode);
		if (result != COMMAND_ERROR_NONE)
			return result;
	}

	return COMMAND_ERROR_NONE;
}

static ServerCommandError
do_disable (ServerHandle *handle, BreakpointInfo *breakpoint)
{
	ServerCommandError result;
	ArchInfo *arch = handle->arch;
	InferiorHandle *inferior = handle->inferior;
	guint32 address;

	if (!breakpoint->enabled)
		return COMMAND_ERROR_NONE;

	address = (guint32) breakpoint->address;

	if (breakpoint->dr_index >= 0) {
		X86_DR_DISABLE (arch, breakpoint->dr_index);

		result = _server_ptrace_set_dr (inferior, breakpoint->dr_index, 0L);
		if (result != COMMAND_ERROR_NONE) {
			g_warning (G_STRLOC ": %d", result);
			return result;
		}

		result = _server_ptrace_set_dr (inferior, DR_CONTROL, arch->dr_control);
		if (result != COMMAND_ERROR_NONE) {
			g_warning (G_STRLOC ": %d", result);
			return result;
		}

		arch->dr_regs [breakpoint->dr_index] = 0;
	} else {
		result = server_ptrace_write_memory (handle, address, 1, &breakpoint->saved_insn);
		if (result != COMMAND_ERROR_NONE)
			return result;
	}

	return COMMAND_ERROR_NONE;
}

static ServerCommandError
server_ptrace_insert_breakpoint (ServerHandle *handle, guint64 address, guint32 *bhandle)
{
	BreakpointInfo *breakpoint;
	ServerCommandError result;

	mono_debugger_breakpoint_manager_lock ();
	breakpoint = (BreakpointInfo *) mono_debugger_breakpoint_manager_lookup (handle->bpm, address);
	if (breakpoint) {
		/*
		 * You cannot have a hardware breakpoint and a normal breakpoint on the same
		 * instruction.
		 */
		if (breakpoint->is_hardware_bpt) {
			mono_debugger_breakpoint_manager_unlock ();
			return COMMAND_ERROR_DR_OCCUPIED;
		}

		breakpoint->refcount++;
		goto done;
	}

	breakpoint = g_new0 (BreakpointInfo, 1);

	breakpoint->refcount = 1;
	breakpoint->address = address;
	breakpoint->is_hardware_bpt = FALSE;
	breakpoint->id = mono_debugger_breakpoint_manager_get_next_id ();
	breakpoint->dr_index = -1;

	result = do_enable (handle, breakpoint);
	if (result != COMMAND_ERROR_NONE) {
		mono_debugger_breakpoint_manager_unlock ();
		g_free (breakpoint);
		return result;
	}

	breakpoint->enabled = TRUE;
	mono_debugger_breakpoint_manager_insert (handle->bpm, (BreakpointInfo *) breakpoint);
 done:
	*bhandle = breakpoint->id;
	mono_debugger_breakpoint_manager_unlock ();

	return COMMAND_ERROR_NONE;
}

static ServerCommandError
server_ptrace_remove_breakpoint (ServerHandle *handle, guint32 bhandle)
{
	BreakpointInfo *breakpoint;
	ServerCommandError result;

	mono_debugger_breakpoint_manager_lock ();
	breakpoint = (BreakpointInfo *) mono_debugger_breakpoint_manager_lookup_by_id (handle->bpm, bhandle);
	if (!breakpoint) {
		result = COMMAND_ERROR_NO_SUCH_BREAKPOINT;
		goto out;
	}

	if (--breakpoint->refcount > 0) {
		result = COMMAND_ERROR_NONE;
		goto out;
	}

	result = do_disable (handle, breakpoint);
	if (result != COMMAND_ERROR_NONE)
		goto out;

	breakpoint->enabled = FALSE;
	mono_debugger_breakpoint_manager_remove (handle->bpm, (BreakpointInfo *) breakpoint);

 out:
	mono_debugger_breakpoint_manager_unlock ();
	return result;
}

static void
x86_arch_remove_hardware_breakpoints (ServerHandle *handle)
{
	int i;

	for (i = 0; i < DR_NADDR; i++) {
		X86_DR_DISABLE (handle->arch, i);

		_server_ptrace_set_dr (handle->inferior, i, 0L);
		_server_ptrace_set_dr (handle->inferior, DR_CONTROL, handle->arch->dr_control);

		handle->arch->dr_regs [i] = 0;
	}
}

static ServerCommandError
server_ptrace_init_after_fork (ServerHandle *handle)
{
	GPtrArray *breakpoints;
	int i;

	mono_debugger_breakpoint_manager_lock ();

	breakpoints = mono_debugger_breakpoint_manager_get_breakpoints (handle->bpm);
	for (i = 0; i < breakpoints->len; i++) {
		BreakpointInfo *info = g_ptr_array_index (breakpoints, i);

		if (info->dr_index >= 0)
			do_disable (handle, info);
	}

	mono_debugger_breakpoint_manager_unlock ();
	return COMMAND_ERROR_NONE;
}

static ServerCommandError
find_free_hw_register (ServerHandle *handle, guint32 *idx)
{
	int i;

	for (i = 0; i < DR_NADDR; i++) {
		if (!handle->arch->dr_regs [i]) {
			*idx = i;
			return COMMAND_ERROR_NONE;
		}
	}

	return COMMAND_ERROR_DR_OCCUPIED;
}

static ServerCommandError
server_ptrace_insert_hw_breakpoint (ServerHandle *handle, guint32 type, guint32 *idx,
				    guint64 address, guint32 *bhandle)
{
	BreakpointInfo *breakpoint;
	ServerCommandError result;

	mono_debugger_breakpoint_manager_lock ();
	breakpoint = (BreakpointInfo *) mono_debugger_breakpoint_manager_lookup (handle->bpm, address);
	if (breakpoint) {
		breakpoint->refcount++;
		goto done;
	}

	result = find_free_hw_register (handle, idx);
	if (result != COMMAND_ERROR_NONE) {
		mono_debugger_breakpoint_manager_unlock ();
		return result;
	}

	breakpoint = g_new0 (BreakpointInfo, 1);
	breakpoint->type = (HardwareBreakpointType) type;
	breakpoint->address = address;
	breakpoint->refcount = 1;
	breakpoint->id = mono_debugger_breakpoint_manager_get_next_id ();
	breakpoint->is_hardware_bpt = TRUE;
	breakpoint->dr_index = *idx;

	result = do_enable (handle, breakpoint);
	if (result != COMMAND_ERROR_NONE) {
		mono_debugger_breakpoint_manager_unlock ();
		g_free (breakpoint);
		return result;
	}

	breakpoint->enabled = TRUE;
	mono_debugger_breakpoint_manager_insert (handle->bpm, (BreakpointInfo *) breakpoint);
 done:
	*bhandle = breakpoint->id;
	mono_debugger_breakpoint_manager_unlock ();

	return COMMAND_ERROR_NONE;
}

static ServerCommandError
server_ptrace_enable_breakpoint (ServerHandle *handle, guint32 bhandle)
{
	BreakpointInfo *breakpoint;
	ServerCommandError result;

	mono_debugger_breakpoint_manager_lock ();
	breakpoint = (BreakpointInfo *) mono_debugger_breakpoint_manager_lookup_by_id (handle->bpm, bhandle);
	if (!breakpoint) {
		mono_debugger_breakpoint_manager_unlock ();
		return COMMAND_ERROR_NO_SUCH_BREAKPOINT;
	}

	result = do_enable (handle, breakpoint);
	breakpoint->enabled = TRUE;
	mono_debugger_breakpoint_manager_unlock ();
	return result;
}

static ServerCommandError
server_ptrace_disable_breakpoint (ServerHandle *handle, guint32 bhandle)
{
	BreakpointInfo *breakpoint;
	ServerCommandError result;

	mono_debugger_breakpoint_manager_lock ();
	breakpoint = (BreakpointInfo *) mono_debugger_breakpoint_manager_lookup_by_id (handle->bpm, bhandle);
	if (!breakpoint) {
		mono_debugger_breakpoint_manager_unlock ();
		return COMMAND_ERROR_NO_SUCH_BREAKPOINT;
	}

	result = do_disable (handle, breakpoint);
	breakpoint->enabled = FALSE;
	mono_debugger_breakpoint_manager_unlock ();
	return result;
}

static ServerCommandError
server_ptrace_get_breakpoints (ServerHandle *handle, guint32 *count, guint32 **retval)
{
	int i;
	GPtrArray *breakpoints;

	mono_debugger_breakpoint_manager_lock ();
	breakpoints = mono_debugger_breakpoint_manager_get_breakpoints (handle->bpm);
	*count = breakpoints->len;
	*retval = g_new0 (guint32, breakpoints->len);

	for (i = 0; i < breakpoints->len; i++) {
		BreakpointInfo *info = g_ptr_array_index (breakpoints, i);

		(*retval) [i] = info->id;
	}
	mono_debugger_breakpoint_manager_unlock ();

	return COMMAND_ERROR_NONE;	
}


static ServerCommandError
server_ptrace_abort_invoke (ServerHandle *handle)
{
	RuntimeInvokeData *rdata;

	rdata = get_runtime_invoke_data (handle->arch);
	if (!rdata)
		return COMMAND_ERROR_UNKNOWN_ERROR;

	if (_server_ptrace_set_registers (handle->inferior, rdata->saved_regs) != COMMAND_ERROR_NONE)
		g_error (G_STRLOC ": Can't restore registers after returning from a call");

	if (_server_ptrace_set_fp_registers (handle->inferior, rdata->saved_fpregs) != COMMAND_ERROR_NONE)
		g_error (G_STRLOC ": Can't restore FP registers after returning from a call");

	handle->inferior->last_signal = rdata->saved_signal;
	g_free (rdata->saved_regs);
	g_free (rdata->saved_fpregs);
	g_ptr_array_remove (handle->arch->rti_stack, rdata);

	x86_arch_get_registers (handle);
	g_free (rdata);

	return COMMAND_ERROR_NONE;
}
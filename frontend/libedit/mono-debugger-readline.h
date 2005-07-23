#ifndef __MONO_DEBUGGER_READLINE_H__
#define __MONO_DEBUGGER_READLINE_H__

#include <glib.h>
#include <stdio.h>
#include <readline.h>

G_BEGIN_DECLS

typedef void (*CompletionDelegate)(const char *text, int start, int end);

extern void mono_debugger_readline_static_init (void);
extern int mono_debugger_readline_is_a_tty (int fd);
extern char *mono_debugger_readline_readline (const char *prompt);
extern void mono_debugger_readline_add_history (const char *line);
extern void mono_debugger_readline_set_completion_matches (char **matches, int count);
extern void mono_debugger_readline_enable_completion (CompletionDelegate cb);
extern char* mono_debugger_readline_current_line_buffer (void);

extern int mono_debugger_readline_get_filename_completion_desired (void);
extern void mono_debugger_readline_set_filename_completion_desired (int v);

extern int mono_debugger_readline_get_columns (void);

G_END_DECLS

#endif

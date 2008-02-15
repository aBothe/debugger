/*
 * mempool.c: efficient memory allocation
 *
 * MonoMemPool is for fast allocation of memory. We free
 * all memory when the pool is destroyed.
 *
 * Author:
 *   Dietmar Maurer (dietmar@ximian.com)
 *
 * (C) 2001 Ximian, Inc.
 */

#include <config.h>
#include <glib.h>
#include <string.h>

#include "mempool.h"

/*
 * MonoMemPool is for fast allocation of memory. We free
 * all memory when the pool is destroyed.
 */

#define MEM_ALIGN 8

#define MONO_MEMPOOL_PAGESIZE 8192
#define MONO_MEMPOOL_MINSIZE 512

#ifndef G_LIKELY
#define G_LIKELY(a) (a)
#define G_UNLIKELY(a) (a)
#endif

struct _MonoMemPool {
	MonoMemPool *next;
	gint rest;
	guint8 *pos, *end;
	guint32 size;
	union {
		double pad; /* to assure proper alignment */
		guint32 allocated;
	} d;
};

/**
 * mono_mempool_new:
 *
 * Returns: a new memory pool.
 */
MonoMemPool *
mono_mempool_new (void)
{
	return mono_mempool_new_size (MONO_MEMPOOL_PAGESIZE);
}

MonoMemPool *
mono_mempool_new_size (int initial_size)
{
	MonoMemPool *pool;
	if (initial_size < MONO_MEMPOOL_MINSIZE)
		initial_size = MONO_MEMPOOL_MINSIZE;
	pool = g_malloc (initial_size);

	pool->next = NULL;
	pool->pos = (guint8*)pool + sizeof (MonoMemPool);
	pool->end = pool->pos + initial_size - sizeof (MonoMemPool);
	pool->d.allocated = pool->size = initial_size;
	return pool;
}

/**
 * mono_mempool_destroy:
 * @pool: the memory pool to destroy
 *
 * Free all memory associated with this pool.
 */
void
mono_mempool_destroy (MonoMemPool *pool)
{
	MonoMemPool *p, *n;

	p = pool;
	while (p) {
		n = p->next;
		g_free (p);
		p = n;
	}
}

/**
 * mono_mempool_invalidate:
 * @pool: the memory pool to invalidate
 *
 * Fill the memory associated with this pool to 0x2a (42). Useful for debugging.
 */
void
mono_mempool_invalidate (MonoMemPool *pool)
{
	MonoMemPool *p, *n;

	p = pool;
	while (p) {
		n = p->next;
		memset (p, 42, p->size);
		p = n;
	}
}

void
mono_mempool_empty (MonoMemPool *pool)
{
	pool->pos = (guint8*)pool + sizeof (MonoMemPool);
	pool->end = pool->pos + pool->size - sizeof (MonoMemPool);
}

/**
 * mono_mempool_stats:
 * @pool: the momory pool we need stats for
 *
 * Print a few stats about the mempool
 */
void
mono_mempool_stats (MonoMemPool *pool)
{
	MonoMemPool *p;
	int count = 0;
	guint32 still_free = 0;

	p = pool;
	while (p) {
		still_free += p->end - p->pos;
		p = p->next;
		count++;
	}
	if (pool) {
		g_print ("Mempool %p stats:\n", pool);
		g_print ("Total mem allocated: %d\n", pool->d.allocated);
		g_print ("Num chunks: %d\n", count);
		g_print ("Free memory: %d\n", still_free);
	}
}

#ifdef TRACE_ALLOCATIONS
#include <execinfo.h>
#include "metadata/appdomain.h"
#include "metadata/metadata-internals.h"

static void
mono_backtrace (int limit)
{
        void *array[limit];
        char **names;
        int i;
        backtrace (array, limit);
        names = backtrace_symbols (array, limit);
        for (i = 1; i < limit; ++i) {
                g_print ("\t%s\n", names [i]);
        }
        g_free (names);
}

#endif

static int
get_next_size (MonoMemPool *pool, int size)
{
	int target = pool->next? pool->next->size: pool->size;
	size += sizeof (MonoMemPool);
	/* increase the size */
	target += target / 2;
	while (target < size) {
		target += target / 2;
	}
	if (target > MONO_MEMPOOL_PAGESIZE)
		target = MONO_MEMPOOL_PAGESIZE;
	/* we are called with size smaller than 4096 */
	g_assert (size <= MONO_MEMPOOL_PAGESIZE);
	return target;
}

/**
 * mono_mempool_alloc:
 * @pool: the momory pool to destroy
 * @size: size of the momory block
 *
 * Allocates a new block of memory in @pool.
 *
 * Returns: the address of a newly allocated memory block.
 */
gpointer
mono_mempool_alloc (MonoMemPool *pool, guint size)
{
	gpointer rval;
	
	size = (size + MEM_ALIGN - 1) & ~(MEM_ALIGN - 1);

	rval = pool->pos;
	pool->pos = (guint8*)rval + size;

#ifdef TRACE_ALLOCATIONS
	if (pool == mono_get_corlib ()->mempool) {
		g_print ("Allocating %d bytes\n", size);
		mono_backtrace (7);
	}
#endif
	if (G_UNLIKELY (pool->pos >= pool->end)) {
		pool->pos -= size;
		if (size >= 4096) {
			MonoMemPool *np = g_malloc (sizeof (MonoMemPool) + size);
			np->next = pool->next;
			pool->next = np;
			np->pos = (guint8*)np + sizeof (MonoMemPool);
			np->size = sizeof (MonoMemPool) + size;
			np->end = np->pos + np->size - sizeof (MonoMemPool);
			pool->d.allocated += sizeof (MonoMemPool) + size;
			return (guint8*)np + sizeof (MonoMemPool);
		} else {
			int new_size = get_next_size (pool, size);
			MonoMemPool *np = g_malloc (new_size);
			np->next = pool->next;
			pool->next = np;
			pool->pos = (guint8*)np + sizeof (MonoMemPool);
			np->pos = (guint8*)np + sizeof (MonoMemPool);
			np->size = new_size;
			np->end = np->pos;
			pool->end = pool->pos + new_size - sizeof (MonoMemPool);
			pool->d.allocated += new_size;

			rval = pool->pos;
			pool->pos += size;
		}
	}

	return rval;
}

/**
 * mono_mempool_alloc0:
 *
 * same as mono_mempool_alloc, but fills memory with zero.
 */
gpointer
mono_mempool_alloc0 (MonoMemPool *pool, guint size)
{
	gpointer rval;
	
	size = (size + MEM_ALIGN - 1) & ~(MEM_ALIGN - 1);

	rval = pool->pos;
	pool->pos = (guint8*)rval + size;

	if (G_UNLIKELY (pool->pos >= pool->end)) {
		rval = mono_mempool_alloc (pool, size);
	}

	memset (rval, 0, size);
	return rval;
}

/**
 * mono_mempool_contains_addr:
 *
 *  Determines whenever ADDR is inside the memory used by the mempool.
 */
gboolean
mono_mempool_contains_addr (MonoMemPool *pool,
							gpointer addr)
{
	MonoMemPool *p;

	p = pool;
	while (p) {
		if (addr > (gpointer)p && addr <= (gpointer)((guint8*)p + p->size))
			return TRUE;
		p = p->next;
	}

	return FALSE;
}

/**
 * mono_mempool_strdup:
 *
 * Same as strdup, but allocates memory from the mempool.
 * Returns: a pointer to the newly allocated string data inside the mempool.
 */
char*
mono_mempool_strdup (MonoMemPool *pool,
					 const char *s)
{
	int l;
	char *res;

	if (s == NULL)
		return NULL;

	l = strlen (s);
	res = mono_mempool_alloc (pool, l + 1);
	memcpy (res, s, l + 1);

	return res;
}

/**
 * mono_mempool_get_allocated:
 *
 * Return the amount of memory allocated for this mempool.
 */
guint32
mono_mempool_get_allocated (MonoMemPool *pool)
{
	return pool->d.allocated;
}
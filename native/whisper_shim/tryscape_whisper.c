// Tryscape on-device speech recognition shim (decision D12, technical-spec S10.2).
// A minimal, stable C ABI over whisper.cpp so the C# side never marshals whisper's version-specific structs.
// Audio arrives as 16 kHz mono float PCM held in memory only; nothing is written to disk.
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>
#include "whisper.h"

#if defined(_WIN32)
#define TW_API __declspec(dllexport)
#else
#define TW_API __attribute__((visibility("default")))
#endif

typedef struct {
    struct whisper_context *ctx;
    atomic_int abort_requested;
} tw_handle;

static void tw_silent_log(enum ggml_log_level level, const char *text, void *user) {
    (void)level; (void)text; (void)user; // never log transcripts or audio details
}

static bool tw_should_abort(void *user) {
    tw_handle *h = (tw_handle *)user;
    return atomic_load(&h->abort_requested) != 0;
}

// Returns an opaque handle, or NULL if the model cannot be loaded.
TW_API void *tw_load(const char *model_path) {
    if (model_path == NULL) return NULL;
    whisper_log_set(tw_silent_log, NULL);
    struct whisper_context_params cparams = whisper_context_default_params();
    cparams.use_gpu = false;
    struct whisper_context *ctx = whisper_init_from_file_with_params(model_path, cparams);
    if (ctx == NULL) return NULL;
    tw_handle *h = (tw_handle *)calloc(1, sizeof(tw_handle));
    if (h == NULL) { whisper_free(ctx); return NULL; }
    h->ctx = ctx;
    atomic_store(&h->abort_requested, 0);
    return h;
}

// Requests that a running tw_transcribe stop early; safe from any thread.
TW_API void tw_abort(void *handle) {
    if (handle != NULL) atomic_store(&((tw_handle *)handle)->abort_requested, 1);
}

// Transcribes English audio. Writes UTF-8 text (NUL-terminated, truncated to out_capacity - 1) into out.
// Returns the number of bytes written, -1 for bad arguments, -2 if whisper failed, -3 if aborted.
// prompt (optional, may be NULL): domain vocabulary passed as whisper's initial prompt to bias recognition.
TW_API int tw_transcribe(void *handle, const float *samples, int sample_count, int threads, const char *prompt,
                         char *out, int out_capacity) {
    if (handle == NULL || samples == NULL || sample_count <= 0 || out == NULL || out_capacity < 1) return -1;
    tw_handle *h = (tw_handle *)handle;
    atomic_store(&h->abort_requested, 0);
    out[0] = '\0';

    struct whisper_full_params p = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    p.n_threads = threads > 0 ? threads : 4;
    p.language = "en";
    p.translate = false;
    p.no_context = true;
    p.no_timestamps = true;
    p.single_segment = false;
    p.print_progress = false;
    p.print_realtime = false;
    p.print_special = false;
    p.print_timestamps = false;
    p.suppress_blank = true;
    p.initial_prompt = (prompt != NULL && prompt[0] != '\0') ? prompt : NULL;
    p.abort_callback = tw_should_abort;
    p.abort_callback_user_data = h;

    int rc = whisper_full(h->ctx, p, samples, sample_count);
    if (atomic_load(&h->abort_requested)) return -3;
    if (rc != 0) return -2;

    int written = 0;
    int segments = whisper_full_n_segments(h->ctx);
    for (int i = 0; i < segments; i++) {
        const char *text = whisper_full_get_segment_text(h->ctx, i);
        if (text == NULL) continue;
        size_t len = strlen(text);
        size_t room = (size_t)(out_capacity - 1 - written);
        if (len > room) len = room;
        memcpy(out + written, text, len);
        written += (int)len;
        if (written >= out_capacity - 1) break;
    }
    out[written] = '\0';
    return written;
}

TW_API void tw_free(void *handle) {
    if (handle == NULL) return;
    tw_handle *h = (tw_handle *)handle;
    whisper_free(h->ctx);
    free(h);
}

TW_API int tw_abi_version(void) { return 2; }

(() => {
    const $ = (id) => document.getElementById(id);
    const form = $('url-form'), urlInput = $('url'), fetchBtn = $('fetch-btn');
    const errorEl = $('error'), result = $('result');
    const progressBox = $('progress-box'), barFill = $('bar-fill'), statusEl = $('status'), manualLink = $('manual-link');

    let currentUrl = '';
    let busy = false;

    function showError(msg) {
        errorEl.textContent = msg || '';
        errorEl.hidden = !msg;
    }

    async function api(path, options = {}) {
        const res = await fetch(path, {
            headers: { 'Content-Type': 'application/json' },
            ...options
        });
        let data = null;
        try { data = await res.json(); } catch { /* no body */ }
        if (!res.ok) throw new Error((data && data.error) || `Request failed (${res.status}).`);
        return data;
    }

    function fmtDuration(total) {
        if (!total) return '';
        const h = Math.floor(total / 3600), m = Math.floor((total % 3600) / 60), s = total % 60;
        const pad = (n) => String(n).padStart(2, '0');
        return h ? `${h}:${pad(m)}:${pad(s)}` : `${m}:${pad(s)}`;
    }

    function setBusy(state) {
        busy = state;
        document.querySelectorAll('.chip').forEach((b) => (b.disabled = state));
        fetchBtn.disabled = state;
    }

    function chip(label, kind, height) {
        const b = document.createElement('button');
        b.type = 'button';
        b.className = 'chip';
        b.textContent = label;
        b.addEventListener('click', () => startDownload(kind, height));
        return b;
    }

    function render(info) {
        $('thumb').src = info.thumbnail;
        $('title').textContent = info.title;               // textContent => no XSS
        $('channel').textContent = info.channel || '';
        $('duration').textContent = fmtDuration(info.durationSeconds);

        const mp4 = $('mp4-list'), mp3 = $('mp3-list');
        mp4.replaceChildren();
        mp3.replaceChildren();

        if (info.heights.length === 0) {
            const p = document.createElement('p');
            p.textContent = 'No video formats available.';
            mp4.append(p);
        }
        info.heights.forEach((h) => mp4.append(chip(`MP4 · ${h}p`, 'mp4', h)));
        mp3.append(chip('MP3 · best quality', 'mp3', null));

        progressBox.hidden = true;
        result.hidden = false;
    }

    form.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (busy) return;
        showError('');
        result.hidden = true;
        fetchBtn.disabled = true;
        fetchBtn.textContent = 'Loading…';

        try {
            currentUrl = urlInput.value.trim();
            const info = await api('/api/info', { method: 'POST', body: JSON.stringify({ url: currentUrl }) });
            render(info);
        } catch (err) {
            showError(err.message);
        } finally {
            fetchBtn.disabled = false;
            fetchBtn.textContent = 'Fetch';
        }
    });

    async function startDownload(kind, height) {
        if (busy) return;
        showError('');
        setBusy(true);
        manualLink.hidden = true;
        progressBox.hidden = false;
        barFill.style.width = '0%';
        statusEl.textContent = 'Starting…';

        try {
            const { jobId } = await api('/api/download', {
                method: 'POST',
                body: JSON.stringify({ url: currentUrl, kind, height })
            });
            await poll(jobId);
        } catch (err) {
            showError(err.message);
            progressBox.hidden = true;
        } finally {
            setBusy(false);
        }
    }

    async function poll(jobId) {
        for (; ;) {
            await new Promise((r) => setTimeout(r, 1000));
            const job = await api(`/api/jobs/${jobId}`);

            barFill.style.width = `${job.progress}%`;
            statusEl.textContent = `${job.stage}… ${job.progress}%`;

            if (job.state === 'failed') throw new Error(job.error || 'Download failed.');

            if (job.state === 'completed') {
                statusEl.textContent = 'Done! Your download is starting.';
                manualLink.href = job.downloadUrl;
                manualLink.hidden = false;

                const a = document.createElement('a');
                a.href = job.downloadUrl;
                a.download = '';
                document.body.append(a);
                a.click();
                a.remove();
                return;
            }
        }
    }
})();
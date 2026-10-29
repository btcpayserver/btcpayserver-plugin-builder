(() => {
    // Let action menus escape the scrollable table, including when a search leaves one row.
    document.querySelectorAll('#AdminUsers [data-bs-toggle="dropdown"]').forEach(button =>
        new bootstrap.Dropdown(button, { popperConfig: { strategy: 'fixed' } }));

    const modal = document.getElementById('UserModerationModal');
    const form = document.getElementById('ModerationForm');
    const reason = document.getElementById('ModerationReason');
    const network = document.getElementById('ModerationNetwork');
    const error = document.getElementById('ModerationError');
    const submit = document.getElementById('ModerationSubmit');
    let pending = false;

    modal.addEventListener('show.bs.modal', event => {
        const button = event.relatedTarget;
        form.reset();
        form.action = button.dataset.moderationAction;
        document.getElementById('ModerationTitle').textContent = button.dataset.title;
        document.getElementById('ModerationTarget').textContent = button.dataset.target || '';
        document.getElementById('ModerationDescription').textContent = button.dataset.description;
        const signupIp = button.dataset.signupIp || '';
        document.getElementById('ModerationSignupIpField').hidden = !signupIp;
        document.getElementById('ModerationSignupIp').textContent = signupIp;
        const needsNetwork = button.dataset.network === 'true';
        document.getElementById('ModerationNetworkField').hidden = !needsNetwork;
        network.disabled = !needsNetwork;
        submit.textContent = button.dataset.submitLabel;
        submit.classList.toggle('btn-danger', button.dataset.danger === 'true');
        submit.classList.toggle('btn-primary', button.dataset.danger !== 'true');
        error.hidden = true;
        error.textContent = '';
    });
    modal.addEventListener('shown.bs.modal', () => (network.disabled ? reason : network).focus());
    modal.addEventListener('hide.bs.modal', event => {
        if (pending) event.preventDefault();
    });

    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (pending || !form.reportValidity()) return;
        pending = true;
        error.hidden = true;
        const label = submit.textContent;
        submit.textContent = 'Applying…';
        form.querySelectorAll('button').forEach(button => button.disabled = true);
        let navigating = false;
        try {
            const data = new FormData(form);
            data.set('returnUrl', window.location.pathname + window.location.search);
            const response = await fetch(form.action, {
                method: 'POST', body: data, credentials: 'same-origin',
                headers: { 'Accept': 'application/json' }
            });
            if (response.redirected) throw new Error('Your session may have expired. Reload the page and try again.');
            const result = await response.json().catch(() => null);
            if (!result) throw new Error('The result could not be confirmed. Reload the page before trying again.');
            if (!response.ok) {
                const validation = result.errors && Object.values(result.errors).flat().join(' ');
                throw new Error(validation || result.message || 'The action could not be applied. Reload the page and try again.');
            }
            if (!result.redirectUrl) throw new Error('The result could not be confirmed. Reload the page before trying again.');
            navigating = true;
            window.location.assign(result.redirectUrl);
        } catch (failure) {
            error.textContent = failure instanceof Error && !(failure instanceof TypeError)
                ? failure.message : 'The result could not be confirmed. Reload the page before trying again.';
            error.hidden = false;
            error.focus();
        } finally {
            if (!navigating) {
                pending = false;
                submit.textContent = label;
                form.querySelectorAll('button').forEach(button => button.disabled = false);
            }
        }
    });
})();

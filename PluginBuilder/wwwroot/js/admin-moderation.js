(() => {
    // Let action menus escape the scrollable table, including when a search leaves one row.
    document.querySelectorAll('#AdminUsers [data-bs-toggle="dropdown"]').forEach(button =>
        new bootstrap.Dropdown(button, { popperConfig: { strategy: 'fixed' } }));

    const modal = document.getElementById('UserModerationModal');
    const form = document.getElementById('ModerationForm');
    const reason = document.getElementById('ModerationReason');
    const network = document.getElementById('ModerationNetwork');
    const banType = document.getElementById('ModerationBanType');
    const banTarget = document.getElementById('ModerationBanTarget');
    const description = document.getElementById('ModerationDescription');
    const error = document.getElementById('ModerationError');
    const submit = document.getElementById('ModerationSubmit');
    let pending = false;
    let creatingBan = false;

    function updateBanType() {
        const isIp = banType.value === 'ip';
        const isGithub = banType.value === 'github';
        document.getElementById('ModerationNetworkField').hidden = !isIp;
        network.disabled = !isIp;
        document.getElementById('ModerationBanTargetField').hidden = isIp;
        banTarget.disabled = isIp;
        document.getElementById('ModerationBanTargetLabel').textContent = isGithub ? 'GitHub username or user ID' : 'Domain';
        banTarget.placeholder = isGithub ? 'username or 12345678' : 'example.com';
        banTarget.maxLength = isGithub ? 40 : 253;
        document.getElementById('ModerationBanTargetHelp').textContent = isGithub
            ? 'Use a numeric user ID to keep the ban across username changes.'
            : 'The domain and its subdomains are included.';
        description.textContent = isIp
            ? 'Refuse new registrations from this address or network. Existing accounts keep access.'
            : isGithub
                ? 'Block this GitHub identity from verification and builds. No IP ban is created.'
                : 'Refuse new registrations from this email domain. Existing accounts keep access. The default blocklist stays active.';
    }

    banType.addEventListener('change', () => {
        banTarget.value = '';
        network.value = '';
        error.hidden = true;
        error.textContent = '';
        updateBanType();
    });

    modal.addEventListener('show.bs.modal', event => {
        const button = event.relatedTarget;
        form.reset();
        creatingBan = button.dataset.createBan === 'true';
        form.action = button.dataset.moderationAction;
        document.getElementById('ModerationTitle').textContent = button.dataset.title;
        const target = document.getElementById('ModerationTarget');
        target.textContent = button.dataset.target || '';
        target.hidden = !target.textContent;
        description.textContent = button.dataset.description || '';
        document.getElementById('ModerationBanTypeField').hidden = !creatingBan;
        banType.disabled = !creatingBan;
        document.getElementById('ModerationBanTargetField').hidden = true;
        banTarget.disabled = true;
        const signupIp = button.dataset.signupIp || '';
        document.getElementById('ModerationSignupIpField').hidden = !signupIp;
        document.getElementById('ModerationSignupIp').textContent = signupIp;
        document.getElementById('ModerationNetworkField').hidden = true;
        network.disabled = true;
        if (creatingBan) updateBanType();
        submit.textContent = button.dataset.submitLabel;
        submit.classList.toggle('btn-danger', button.dataset.danger === 'true');
        submit.classList.toggle('btn-primary', button.dataset.danger !== 'true');
        error.hidden = true;
        error.textContent = '';
    });
    modal.addEventListener('shown.bs.modal', () => (creatingBan ? banType : reason).focus());
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
            if (creatingBan && banType.value === 'ip') data.set('Target', network.value);
            data.set('returnUrl', window.location.pathname + window.location.search);
            banType.disabled = true;
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
                banType.disabled = !creatingBan;
                form.querySelectorAll('button').forEach(button => button.disabled = false);
            }
        }
    });
})();

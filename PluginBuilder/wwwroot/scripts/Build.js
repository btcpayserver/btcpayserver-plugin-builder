(function () {
    window.hljs.highlightAll();

    const $logs = document.getElementById('Logs');
    const fullBuildId = $logs.dataset.buildId;
    const $buildInfo = document.getElementById('BuildInfo');
    const $manifestInfo = document.getElementById('ManifestInfo');

    const connection = new signalR.HubConnectionBuilder()
        .withUrl(`${window.location}/hub`)
        .withAutomaticReconnect()
        .build();

    function mergeJson($elem, json) {
        try {
            const current = JSON.parse($elem.innerText.trim() || '{}');
            const newInfo = JSON.parse(json);
            $elem.innerText = JSON.stringify({...current, ...newInfo}, null, 2);
            window.hljs.highlightAll();
        } catch (err) {
            console.error(err);
        }
    }

    connection.on('build-changed', event => {
        if (event.fullBuildId !== fullBuildId) return;
        const {eventName, buildInfo, manifestInfo} = event;
        if (['failed', 'uploaded', 'removed'].includes(eventName)) {
            return window.location.reload();
        }
        const progressMessage = {
            'queued': 'Build queued...',
            'running': 'Build running...',
            'waiting-upload': 'Waiting to upload...',
            'uploading': 'Uploading build...'
        }[eventName];
        if (progressMessage) {
            const $state = document.getElementById('BuildState');
            const $progress = document.getElementById('BuildProgressMessage');
            if ($state) $state.textContent = eventName;
            if ($progress) $progress.textContent = progressMessage;
        }
        if (buildInfo) {
            mergeJson($buildInfo, buildInfo);
        }
        if (manifestInfo) {
            mergeJson($manifestInfo, manifestInfo);
        }
    });

    connection.on('build-log-updated', event => {
        if (event.fullBuildId !== fullBuildId) return;
        document.getElementById('BuildLogPlaceholder')?.remove();
        if ($logs.innerText.trim().length) {
            $logs.innerText += '\n';
        }
        $logs.innerText += event.log;
    });

    connection.start()
        .catch(console.error);
})();

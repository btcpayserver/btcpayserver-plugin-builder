(function () {
    window.hljs.highlightAll();

    const $logs = document.getElementById('Logs');
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

    connection.on('build-changed', ({eventName, buildInfo, manifestInfo}) => {
        if (['running', 'failed', 'uploaded', 'removed'].includes(eventName)) {
            return window.location.reload();
        }
        if (buildInfo) {
            mergeJson($buildInfo, buildInfo);
        }
        if (manifestInfo) {
            mergeJson($manifestInfo, manifestInfo);
        }
    });

    connection.on('build-log-updated', event => {
        if ($logs.innerText.trim().length) {
            $logs.innerText += '\n';
        }
        $logs.innerText += event.log;
    });

    connection.start()
        .catch(console.error);
})();

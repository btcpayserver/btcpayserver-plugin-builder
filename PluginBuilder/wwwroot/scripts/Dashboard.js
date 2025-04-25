(function () {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl(`${window.location.origin}${window.location.pathname.replace(/\/$/, '')}/hub`)
        .withAutomaticReconnect()
        .build();

    connection.on('build-changed', ({eventName}) => {
        if (['running', 'failed', 'uploaded', 'removed'].includes(eventName)) {
            window.location.reload();
        }
    });

    connection.start()
        .catch(console.error);
})();

(function() {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl(`${window.location}/hub`)
        .withAutomaticReconnect()
        .build();

    connection.on('build-changed', ({ eventName }) => {
        if (['running', 'failed', 'uploaded'].includes(eventName)) {
            window.location.reload();
        }
    });

    connection.start()
        .catch(console.error);
})();

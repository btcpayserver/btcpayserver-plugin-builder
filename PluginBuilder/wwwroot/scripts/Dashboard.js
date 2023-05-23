(function() {
    const connection = new signalR.HubConnectionBuilder()
        .withUrl(`${window.location}/hub`)
        .withAutomaticReconnect()
        .build();

    connection.on("BuildUpdated", () => {
        window.location.reload();
    });

    connection.start()
        .catch(console.error);
})();

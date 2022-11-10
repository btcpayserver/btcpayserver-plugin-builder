var connection = new signalR.HubConnectionBuilder().withUrl(window.location + "/hub").build();

connection.on("BuildUpdated", function () {
    window.location.reload();
});

connection.start();

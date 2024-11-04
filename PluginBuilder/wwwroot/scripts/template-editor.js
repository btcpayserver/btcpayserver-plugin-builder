document.addEventListener('DOMContentLoaded', function () {
    new TomSelect('#Tags', {
        create: true,
        delimiter: ',',
        persist: false,
        maxItems: null
    });
});

document.addEventListener('DOMContentLoaded', function () {
    new TomSelect('#TextSeperator', {
        create: true,
        delimiter: ',',
        persist: false,
        maxItems: null
    });
});

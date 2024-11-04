document.addEventListener('DOMContentLoaded', function () {
    new TomSelect('#Tags', {
        create: true, // Allows new tags to be created
        delimiter: ',',
        persist: false, // Prevents duplicates
        maxItems: null // Allows multiple selections
    });
});

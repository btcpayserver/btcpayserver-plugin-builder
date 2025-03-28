document.addEventListener("DOMContentLoaded", function () {
    const searchInput = document.querySelector('input[name="SearchText"]');
    searchInput.addEventListener("input", function () {
        if (this.value.trim() === "") {
            window.location.href = window.location.pathname;
        }
    });
});

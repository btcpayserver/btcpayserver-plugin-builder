document.addEventListener('DOMContentLoaded', function () {
    new TomSelect('#TextSeperator', {
        create: true,
        delimiter: ',',
        persist: false,
        maxItems: null
    });
});

document.getElementById("TextSeperator").addEventListener("change", function () {
    validateEmails();
});

document.getElementById("emailForm").addEventListener("submit", function (event) {
    if (!validateEmails()) {
        event.preventDefault();
    }
});

function validateEmails() {
    const emailField = document.getElementById("TextSeperator");
    const emailError = document.getElementById("emailError");
    const emailRegex = /^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$/;
    const emails = emailField.value.split(/[,;\s]+/);
    let allValid = true;
    emailError.textContent = "";
    emails.forEach(email => {
        if (email && !emailRegex.test(email.trim())) {
            allValid = false;
        }
    });
    if (!allValid) {
        emailError.textContent = "Please enter valid email addresses";
        emailError.style.color = "red";
    }
    return allValid;
}

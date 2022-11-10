var triggerEl = document.querySelector('#artifacts-tabs li:first-child button');
bootstrap.Tab.getOrCreateInstance(triggerEl).show();
hljs.highlightAll();

var term = new Terminal({
    cursorBlink : false,
    disableStdin: true,
    allowTransparency : false,
    screenReaderMode: true,
    rows: 300
});

var logsElem = document.getElementById('terminal');
var txt = logsElem.innerText;
logsElem.innerText = "";
term.open(logsElem);
term.write(txt);

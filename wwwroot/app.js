"use strict";

const board = document.querySelector("#board");
const ctx = board.getContext("2d", { alpha: false });
const paletteElement = document.querySelector("#palette");
const cooldownElement = document.querySelector("#cooldown");
const messageElement = document.querySelector("#message");
const connectionElement = document.querySelector("#connection");
const coordsElement = document.querySelector("#coords");

let palette = [];
let selectedColor = 1;
let pixels = null;
let nextAllowedAt = 0; // Converted to the browser's clock when received.
let syncing = false;
let queuedChanges = [];
let placing = false;

function tell(message) { messageElement.textContent = message; }
function setConnection(text, live) {
    connectionElement.textContent = text;
    connectionElement.classList.toggle("live", live);
}
function setCooldownFromServer(data) {
    nextAllowedAt = Date.now() + Math.max(0, data.nextAllowedAtUnixMs - data.serverNowUnixMs);
    updateCountdown();
}
function updateCountdown() {
    const seconds = Math.max(0, Math.ceil((nextAllowedAt - Date.now()) / 1000));
    cooldownElement.textContent = seconds ? `${seconds}s` : "Ready!";
    cooldownElement.classList.toggle("ready", seconds === 0);
}
setInterval(updateCountdown, 250);

function makePalette() {
    paletteElement.replaceChildren();
    palette.forEach((hex, index) => {
        const button = document.createElement("button");
        button.className = "swatch" + (index === selectedColor ? " selected" : "");
        button.style.backgroundColor = hex;
        button.type = "button";
        button.title = `Color ${index + 1}: ${hex}`;
        button.setAttribute("aria-label", button.title);
        button.setAttribute("aria-pressed", String(index === selectedColor));
        button.addEventListener("click", () => {
            selectedColor = index;
            for (const item of paletteElement.children) {
                const selected = item === button;
                item.classList.toggle("selected", selected);
                item.setAttribute("aria-pressed", String(selected));
            }
        });
        paletteElement.append(button);
    });
}

function redraw() {
    // One screen pixel per logical pixel. CSS handles pixel-perfect scaling.
    const image = ctx.createImageData(board.width, board.height);
    const rgb = palette.map(hex => [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16)));
    for (let i = 0; i < pixels.length; i++) {
        const [r, g, b] = rgb[pixels[i]] || rgb[0];
        image.data[i * 4] = r;
        image.data[i * 4 + 1] = g;
        image.data[i * 4 + 2] = b;
        image.data[i * 4 + 3] = 255;
    }
    ctx.putImageData(image, 0, 0);
}

function applyPixel(x, y, color) {
    if (!pixels || x < 0 || x >= board.width || y < 0 || y >= board.height || !palette[color]) return;
    pixels[y * board.width + x] = color;
    ctx.fillStyle = palette[color];
    ctx.fillRect(x, y, 1, 1);
}

async function refreshState() {
    syncing = true;
    queuedChanges = [];
    try {
        const response = await fetch("/api/state", { cache: "no-store", credentials: "same-origin" });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        const data = await response.json();
        board.width = data.width;
        board.height = data.height;
        palette = data.palette;
        pixels = Uint8Array.from(atob(data.pixels), c => c.charCodeAt(0));
        makePalette();
        redraw();
        setCooldownFromServer(data);
        tell("");
    } finally {
        // Deliver updates received while the snapshot was being fetched.
        for (const [x, y, color] of queuedChanges) applyPixel(x, y, color);
        syncing = false;
        queuedChanges = [];
    }
}

function pixelFromPointer(event) {
    const bounds = board.getBoundingClientRect();
    return {
        x: Math.floor((event.clientX - bounds.left) * board.width / bounds.width),
        y: Math.floor((event.clientY - bounds.top) * board.height / bounds.height)
    };
}

board.addEventListener("pointermove", event => {
    const { x, y } = pixelFromPointer(event);
    coordsElement.textContent = `${x}, ${y}`;
});
board.addEventListener("pointerleave", () => { coordsElement.textContent = "—, —"; });

board.addEventListener("pointerdown", async event => {
    if (event.button !== 0 || !pixels || placing) return;
    if (Date.now() < nextAllowedAt) {
        tell("Your next pixel isn't ready yet.");
        return;
    }
    const { x, y } = pixelFromPointer(event);
    if (x < 0 || y < 0 || x >= board.width || y >= board.height) return;

    placing = true;
    try {
        const response = await fetch("/api/pixel", {
            method: "POST",
            credentials: "same-origin",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ x, y, color: selectedColor })
        });
        const data = await response.json();
        if (response.status === 429) {
            setCooldownFromServer(data);
            tell("Still cooling down. Try again soon.");
        } else if (!response.ok) {
            tell(data.error || `Couldn't place pixel (HTTP ${response.status}).`);
        } else {
            setCooldownFromServer(data);
            applyPixel(x, y, selectedColor);
            tell(`Placed pixel at ${x}, ${y}.`);
        }
    } catch (error) {
        tell("Unable to reach the server.");
        console.error(error);
    } finally {
        placing = false;
    }
});

async function startRealtime() {
    if (!window.signalR) {
        setConnection("SignalR library missing", false);
        tell("Missing /lib/signalr.min.js — see README.md.");
        return;
    }
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/hub")
        .withAutomaticReconnect()
        .build();

    connection.on("PixelChanged", (x, y, color) => {
        if (syncing) queuedChanges.push([x, y, color]);
        else applyPixel(x, y, color);
    });
    connection.onreconnecting(() => setConnection("Reconnecting…", false));
    connection.onreconnected(async () => {
        setConnection("Live", true);
        try { await refreshState(); } catch (error) { console.error(error); }
    });
    connection.onclose(() => { setConnection("Disconnected", false); setTimeout(connect, 5000); });

    async function connect() {
        try {
            await connection.start();
            setConnection("Live", true);
            // Closes the gap between initial state fetch and hub connection.
            await refreshState();
        } catch (error) {
            setConnection("Retrying connection…", false);
            console.error(error);
            setTimeout(connect, 5000);
        }
    }
    await connect();
}

(async () => {
    try {
        await refreshState();
        void startRealtime();
    } catch (error) {
        setConnection("Server unavailable", false);
        tell("Could not load the canvas. Check the application logs.");
        console.error(error);
    }
})();

const elements = {
    modeBadge: document.querySelector("#modeBadge"),
    currentTime: document.querySelector("#currentTime"),
    historyPosition: document.querySelector("#historyPosition"),
    xAxis: document.querySelector("#xAxis"),
    yAxis: document.querySelector("#yAxis"),
    grid: document.querySelector("#warehouseGrid"),
    loadedPallets: document.querySelector("#loadedPallets"),
    statusMessage: document.querySelector("#statusMessage"),
    unloadedStacks: document.querySelector("#unloadedStacks"),
    unloadBatches: document.querySelector("#unloadBatches"),
    fullStacks: document.querySelector("#fullStacks"),
    accessibleFullStacks: document.querySelector("#accessibleFullStacks"),
    readyStacks: document.querySelector("#readyStacks"),
    activeCycle: document.querySelector("#activeCycle"),
    nextLoadIn: document.querySelector("#nextLoadIn"),
    nextUnloadCycleIn: document.querySelector("#nextUnloadCycleIn"),
    nextUnloadStackIn: document.querySelector("#nextUnloadStackIn"),
    playPauseButton: document.querySelector("#playPauseButton"),
    previousButton: document.querySelector("#previousButton"),
    nextButton: document.querySelector("#nextButton"),
    resetButton: document.querySelector("#resetButton"),
    settingsForm: document.querySelector("#settingsForm"),
    loadSeconds: document.querySelector("#loadSeconds"),
    unloadCycleSeconds: document.querySelector("#unloadCycleSeconds"),
    unloadStackSeconds: document.querySelector("#unloadStackSeconds"),
    selectedStack: document.querySelector("#selectedStack"),
    eventList: document.querySelector("#eventList")
};

const minimumOperationSeconds = 0.05;
const settingsInputs = [
    elements.loadSeconds,
    elements.unloadCycleSeconds,
    elements.unloadStackSeconds
];

let selectedKey = null;
let lastSnapshot = null;
let hasUnsavedSettings = false;

elements.playPauseButton.addEventListener("click", async () => {
    if (!lastSnapshot || lastSnapshot.isRunning) {
        await postControl("pause");
    } else {
        await postControl("play");
    }
});

elements.previousButton.addEventListener("click", () => postControl("previous"));
elements.nextButton.addEventListener("click", () => postControl("next"));
elements.resetButton.addEventListener("click", async () => {
    selectedKey = null;
    hasUnsavedSettings = false;
    await postControl("reset");
});

for (const input of settingsInputs) {
    input.addEventListener("input", () => {
        hasUnsavedSettings = true;
    });
}

elements.settingsForm.addEventListener("submit", async event => {
    event.preventDefault();

    const loadSeconds = Number(elements.loadSeconds.value);
    const unloadCycleSeconds = Number(elements.unloadCycleSeconds.value);
    const unloadStackSeconds = Number(elements.unloadStackSeconds.value);

    if (
        !Number.isFinite(loadSeconds) ||
        !Number.isFinite(unloadCycleSeconds) ||
        !Number.isFinite(unloadStackSeconds) ||
        loadSeconds < minimumOperationSeconds ||
        unloadCycleSeconds < minimumOperationSeconds ||
        unloadStackSeconds < minimumOperationSeconds
    ) {
        elements.statusMessage.textContent = "Minimalny czas operacji to 0.05 s.";
        return;
    }

    const response = await fetch("/api/settings", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ loadSeconds, unloadCycleSeconds, unloadStackSeconds })
    });

    const payload = await response.json();
    if (!response.ok) {
        elements.statusMessage.textContent = payload.message ?? "Nie mozna zapisac ustawien.";
        return;
    }

    hasUnsavedSettings = false;
    render(payload);
});

async function postControl(command) {
    const response = await fetch(`/api/control/${command}`, { method: "POST" });
    render(await response.json());
}

async function refresh() {
    const response = await fetch("/api/simulation");
    render(await response.json());
}

function render(snapshot) {
    lastSnapshot = snapshot;
    renderStatus(snapshot);
    renderAxes(snapshot);
    renderGrid(snapshot);
    renderEvents(snapshot.events);
    renderSelected(snapshot);
}

function renderStatus(snapshot) {
    elements.modeBadge.textContent = snapshot.isBlocked
        ? "Blokada"
        : snapshot.isRunning
            ? "Czas działa"
            : "Pauza";
    elements.modeBadge.className = `mode-badge ${snapshot.mode}`;
    elements.currentTime.textContent = snapshot.currentTime;
    elements.historyPosition.textContent = `${snapshot.historyIndex}/${snapshot.historyCount}`;
    elements.loadedPallets.textContent = snapshot.loadedPallets;
    elements.statusMessage.textContent = snapshot.blockMessage ?? "OK";
    elements.unloadedStacks.textContent = snapshot.unloadedStacks;
    elements.unloadBatches.textContent = snapshot.unloadBatches;
    elements.fullStacks.textContent = snapshot.fullStacks;
    elements.accessibleFullStacks.textContent = snapshot.accessibleFullStacks;
    elements.readyStacks.textContent = `${snapshot.readyUnloadStacks}/${snapshot.unloadBatchSize}`;
    elements.activeCycle.textContent = snapshot.isUnloadCycleActive
        ? `${snapshot.activeUnloadCompleted}/${snapshot.unloadBatchSize}`
        : "brak";
    elements.nextLoadIn.textContent = snapshot.nextLoadIn;
    elements.nextUnloadCycleIn.textContent = snapshot.nextUnloadCycleIn;
    elements.nextUnloadStackIn.textContent = snapshot.nextUnloadStackIn;
    elements.playPauseButton.textContent = snapshot.isRunning ? "Pauza" : "Start";
    elements.playPauseButton.disabled = snapshot.isBlocked;
    elements.previousButton.disabled = !snapshot.canStepBackward;

    if (shouldSyncSettingsInputs()) {
        elements.loadSeconds.value = trimNumber(snapshot.loadIntervalSeconds);
        elements.unloadCycleSeconds.value = trimNumber(snapshot.unloadCycleIntervalSeconds);
        elements.unloadStackSeconds.value = trimNumber(snapshot.unloadStackIntervalSeconds);
    }
}

function shouldSyncSettingsInputs() {
    return !hasUnsavedSettings && !elements.settingsForm.contains(document.activeElement);
}

function renderAxes(snapshot) {
    if (elements.xAxis.childElementCount !== snapshot.width) {
        elements.xAxis.replaceChildren(
            ...Array.from({ length: snapshot.width }, (_, index) => {
                const label = document.createElement("span");
                label.textContent = `X${index + 1}`;
                return label;
            })
        );
    }

    if (elements.yAxis.childElementCount !== snapshot.depth) {
        elements.yAxis.replaceChildren(
            ...Array.from({ length: snapshot.depth }, (_, index) => {
                const label = document.createElement("span");
                label.textContent = `Y${snapshot.depth - index}`;
                return label;
            })
        );
    }
}

function renderGrid(snapshot) {
    const fragment = document.createDocumentFragment();

    for (const cell of snapshot.cells) {
        const key = cellKey(cell);
        const button = document.createElement("button");
        button.type = "button";
        button.className = [
            "stack-cell",
            cell.isAccessible ? "accessible" : "blocked",
            cell.isFull ? "full" : "",
            cell.isReliefUnloadReady ? "relief-ready" : "",
            cell.isInUnloadPlan ? "unload-plan" : "",
            cell.isNextUnload ? "next-unload" : "",
            selectedKey === key ? "selected" : ""
        ].filter(Boolean).join(" ");
        button.style.gridColumn = String(cell.x + 1);
        button.style.gridRow = String(cell.y + 1);
        button.dataset.key = key;
        button.setAttribute("aria-label", `Pozycja X${cell.displayX}, Y${cell.displayY}`);
        button.addEventListener("click", () => {
            selectedKey = key;
            render(snapshot);
        });

        button.append(
            cellHeader(cell),
            stackVisual(cell.pallets),
            countRow(cell),
            flagsRow(cell),
            orderLine(cell)
        );

        fragment.append(button);
    }

    elements.grid.replaceChildren(fragment);
}

function cellHeader(cell) {
    const header = document.createElement("div");
    header.className = "cell-top";

    const coordinates = document.createElement("span");
    coordinates.textContent = `X${cell.displayX} / Y${cell.displayY}`;

    const height = document.createElement("span");
    height.className = "height-badge";
    height.textContent = `${cell.height}/7`;

    header.append(coordinates, height);
    return header;
}

function stackVisual(pallets) {
    const visual = document.createElement("div");
    visual.className = "stack-visual";

    if (pallets.length === 0) {
        const empty = document.createElement("span");
        empty.className = "empty-stack";
        empty.textContent = "pusto";
        visual.append(empty);
        return visual;
    }

    for (const pallet of pallets) {
        const block = document.createElement("span");
        block.className = `pallet-bar pallet-${pallet.type.toLowerCase()}`;
        block.style.flexGrow = String(pallet.height);
        block.textContent = `${pallet.type}#${pallet.id}`;
        block.title = `Wjazd: ${pallet.loadedAt}, czeka: ${formatWait(pallet.waitSeconds)}`;
        visual.append(block);
    }

    return visual;
}

function countRow(cell) {
    const row = document.createElement("div");
    row.className = "count-row";
    row.append(
        countBadge("A", cell.countA),
        countBadge("B", cell.countB),
        countBadge("C", cell.countC)
    );
    return row;
}

function countBadge(label, value) {
    const badge = document.createElement("span");
    badge.textContent = `${label}: ${value}`;
    return badge;
}

function flagsRow(cell) {
    const row = document.createElement("div");
    row.className = "flags-row";

    const access = document.createElement("span");
    access.className = cell.isAccessible ? "accessible" : "blocked";
    access.textContent = cell.isAccessible ? "dostępna" : "zablokowana";
    row.append(access);

    if (cell.isFull) {
        const full = document.createElement("span");
        full.textContent = "pełna";
        row.append(full);
    } else if (cell.isReliefUnloadReady) {
        const relief = document.createElement("span");
        relief.className = "relief";
        relief.textContent = "6/7 awaryjny";
        row.append(relief);
    }

    if (cell.isNextUnload) {
        const next = document.createElement("span");
        next.className = "fifo";
        next.textContent = "następna FIFO";
        row.append(next);
    } else if (cell.isInUnloadPlan) {
        const plan = document.createElement("span");
        plan.textContent = "w planie 9";
        row.append(plan);
    }

    return row;
}

function orderLine(cell) {
    const line = document.createElement("div");
    line.className = "order-line";
    line.title = `Dół -> góra: ${cell.orderBottomToTop}`;
    line.textContent = `Dół -> góra: ${cell.orderBottomToTop}`;
    return line;
}

function renderSelected(snapshot) {
    const selected = snapshot.cells.find(cell => cellKey(cell) === selectedKey);

    if (!selected) {
        elements.selectedStack.textContent = "Kliknij pozycję w magazynie.";
        return;
    }

    const wrapper = document.createElement("div");
    const title = document.createElement("p");
    const titleStrong = document.createElement("strong");
    titleStrong.textContent = `X${selected.displayX} / Y${selected.displayY}`;
    title.append(titleStrong, ` - wysokość ${selected.height}/7`);

    const counts = document.createElement("p");
    counts.textContent = `Palety: A=${selected.countA}, B=${selected.countB}, C=${selected.countC}.`;

    const order = document.createElement("p");
    order.textContent = `Kolejność od dołu do góry: ${selected.orderBottomToTop}.`;

    const access = document.createElement("p");
    access.textContent = selected.isAccessible
        ? "Pozycja jest dostępna od strony N."
        : "Pozycja jest zasłonięta przez inne stacki.";

    const fifo = document.createElement("p");
    fifo.textContent = selected.pallets.length === 0
        ? "FIFO: brak palet."
        : `FIFO: najstarsza paleta czeka ${formatWait(selected.oldestWaitSeconds)}, suma wieku stacka ${formatWait(selected.fifoScore)}.`;

    wrapper.append(title, stackVisual(selected.pallets), counts, order, access, fifo, palletList(selected.pallets));
    elements.selectedStack.replaceChildren(wrapper);
}

function palletList(pallets) {
    const list = document.createElement("ol");
    list.className = "pallet-list";

    for (const pallet of pallets) {
        const item = document.createElement("li");
        item.textContent = `${pallet.type}#${pallet.id}, wysokość ${pallet.height}, wjazd ${pallet.loadedAt}, czeka ${formatWait(pallet.waitSeconds)}`;
        list.append(item);
    }

    return list;
}

function renderEvents(events) {
    elements.eventList.replaceChildren(
        ...events.map(event => {
            const item = document.createElement("li");
            item.className = event.kind;

            const time = document.createElement("time");
            time.textContent = `${event.time} · #${event.number}`;

            const text = document.createElement("span");
            text.textContent = event.text;

            item.append(time, text);
            return item;
        })
    );
}

function cellKey(cell) {
    return `${cell.x}:${cell.y}`;
}

function trimNumber(value) {
    return Number(value).toFixed(3).replace(/\.?0+$/, "");
}

function formatWait(seconds) {
    const safeSeconds = Math.max(0, Math.floor(Number(seconds) || 0));
    const minutes = Math.floor(safeSeconds / 60);
    const rest = safeSeconds % 60;

    if (minutes >= 60) {
        const hours = Math.floor(minutes / 60);
        const minutePart = minutes % 60;
        return `${hours}h ${minutePart}m`;
    }

    return `${minutes}m ${rest}s`;
}

refresh();
setInterval(refresh, 100);

const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
const printer = document.querySelector(".printer");
const replayPrint = document.querySelector(".print-replay");
const demos = new Map();
const visible = new Set();
const pending = new Set();
let playing = false;

async function playNext() {
  if (playing || document.hidden) return;

  const next = pending.values().next().value ??
    (!reducedMotion.matches && [...demos.values()].find(demo => demo.autoPlay && visible.has(demo)));
  if (!next) return;

  pending.delete(next);
  next.autoPlay = false;
  playing = true;
  try {
    await next.play();
  } finally {
    playing = false;
    playNext();
  }
}

function queue(action) {
  pending.add(action);
  playNext();
}

function waitForPrint() {
  return Promise.allSettled(printer.getAnimations({ subtree: true }).map(animation => animation.finished));
}

const printReplay = {
  autoPlay: true,
  play() {
    if (reducedMotion.matches) return;
    printer.dataset.printing = "true";
    for (const animation of printer.getAnimations({ subtree: true })) {
      animation.currentTime = 0;
      animation.play();
    }
    return waitForPrint();
  }
};
demos.set(printer, printReplay);
replayPrint.hidden = false;
replayPrint.addEventListener("click", () => queue(printReplay));

for (const demo of document.querySelectorAll("[data-demo]")) {
  const play = demo.querySelector("button");
  const status = demo.querySelector('[role="status"]');
  const problem = demo.querySelector("h3").textContent + ". The job cannot reach the printer.";

  function showReady() {
    demo.dataset.delivered = "true";
    delete demo.dataset.running;
    status.textContent = "Ready to print. " + demo.querySelector(".demo-foot p").textContent;
    play.textContent = reducedMotion.matches ? "Show problem" : "↻ Replay";
  }

  function start() {
    const showProblem = reducedMotion.matches && demo.dataset.phase === "solution";
    delete demo.dataset.running;
    delete demo.dataset.delivered;
    demo.dataset.phase = "problem";
    status.textContent = problem;

    if (reducedMotion.matches) {
      play.textContent = "Show solution";
      if (!showProblem) {
        demo.dataset.phase = "solution";
        showReady();
      }
      return;
    }

    play.textContent = "↻ Replay";
    setTimeout(() => { demo.dataset.running = "true"; }, 40);
    setTimeout(() => {
      delete demo.dataset.running;
      demo.dataset.phase = "solution";
      status.textContent = "Sending print job.";
    }, 1450);
    setTimeout(() => { demo.dataset.running = "true"; }, 1520);
    return new Promise(resolve => setTimeout(() => {
      showReady();
      resolve();
    }, 2850));
  }

  const action = { play: start, autoPlay: true };
  demos.set(demo.querySelector(".scene"), action);
  play.addEventListener("click", () => queue(action));
  if (reducedMotion.matches) play.textContent = "Show solution";
}

const observer = new IntersectionObserver(entries => {
  for (const entry of entries) {
    const action = demos.get(entry.target);
    if (entry.intersectionRatio >= 0.5) {
      visible.add(action);
    } else {
      visible.delete(action);
      if (!entry.isIntersecting) action.autoPlay = true;
    }
  }
  playNext();
}, { threshold: [0, 0.5] });
for (const target of demos.keys()) observer.observe(target);

document.addEventListener("visibilitychange", playNext);
reducedMotion.addEventListener("change", playNext);

const tablist = document.querySelector(".tabs");
const tabs = [...tablist.querySelectorAll("button")];
const panels = tabs.map(tab => document.getElementById(tab.getAttribute("aria-controls")));
const horizontalTabs = matchMedia("(max-width: 740px)");
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");

function updateOrientation() {
  tablist.setAttribute("aria-orientation", horizontalTabs.matches ? "horizontal" : "vertical");
}

function selectTab(index) {
  tabs.forEach((tab, position) => {
    const selected = position === index;
    tab.setAttribute("aria-selected", selected);
    tab.tabIndex = selected ? 0 : -1;
    panels[position].hidden = !selected;
  });
}

tablist.setAttribute("role", "tablist");
updateOrientation();
horizontalTabs.addEventListener("change", updateOrientation);
tabs.forEach((tab, index) => {
  tab.setAttribute("role", "tab");
  panels[index].setAttribute("role", "tabpanel");
  panels[index].setAttribute("aria-labelledby", tab.id);
  panels[index].tabIndex = 0;
  tab.addEventListener("click", () => selectTab(index));
});

tablist.addEventListener("keydown", event => {
  const current = tabs.indexOf(event.target);
  if (current < 0) return;

  const previousKey = horizontalTabs.matches ? "ArrowLeft" : "ArrowUp";
  const nextKey = horizontalTabs.matches ? "ArrowRight" : "ArrowDown";
  const destination = {
    [previousKey]: (current + tabs.length - 1) % tabs.length,
    [nextKey]: (current + 1) % tabs.length,
    Home: 0,
    End: tabs.length - 1,
  };
  const next = destination[event.key];
  if (next === undefined) return;

  event.preventDefault();
  selectTab(next);
  tabs[next].focus();
});

selectTab(0);
tablist.hidden = false;

const reviewDemo = document.querySelector(".review-demo");
const reviewItems = [...reviewDemo.querySelectorAll("input")];
const reviewCount = reviewDemo.querySelector("output");

function updateReviewCount() {
  const selected = reviewItems.filter(item => item.checked).length;
  reviewCount.value = `${selected} of ${reviewItems.length} selected in this example`;
}

updateReviewCount();
reviewCount.hidden = false;
reviewDemo.addEventListener("change", updateReviewCount);

if ("IntersectionObserver" in window && "animate" in Element.prototype) {
  const easeOut = getComputedStyle(document.documentElement).getPropertyValue("--ease-out").trim();
  const observer = new IntersectionObserver(entries => {
    for (const { target, isIntersecting } of entries) {
      if (!isIntersecting) continue;
      observer.unobserve(target);
      if (reducedMotion.matches) continue;

      const isPanel = target.dataset.reveal === "panel";
      target.animate(
        [
          { opacity: isPanel ? .62 : .45, transform: `translateY(${isPanel ? 14 : 20}px)` },
          { opacity: 1, transform: "translateY(0)" },
        ],
        { duration: isPanel ? 740 : 560, easing: easeOut },
      );
    }
  }, { threshold: .15 });

  const sections = document.querySelectorAll("[data-reveal]");
  sections.forEach(section => observer.observe(section));
  reducedMotion.addEventListener("change", () => {
    if (reducedMotion.matches) {
      sections.forEach(section => section.getAnimations().forEach(animation => animation.cancel()));
    }
  });
}

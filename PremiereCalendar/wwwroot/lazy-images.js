const lazyImageSelector = "img[data-lazy-image][data-lazy-src]";

function findLazyImages(roots) {
  const images = [];

  for (const root of roots) {
    if (!(root instanceof Element) && !(root instanceof Document)) {
      continue;
    }

    if (root instanceof Element && root.matches(lazyImageSelector)) {
      images.push(root);
    }

    images.push(...root.querySelectorAll(lazyImageSelector));
  }

  return images;
}

function loadImage(image) {
  const source = image.dataset.lazySrc;
  if (!source || image.dataset.lazyLoaded === "true") {
    return;
  }

  image.dataset.lazyLoaded = "true";
  image.src = source;
}

function observeLazyImages(roots = [document]) {
  const images = findLazyImages(roots);

  if (!("IntersectionObserver" in window)) {
    images.forEach(loadImage);
    return;
  }

  if (!window.premiereCalendarLazyImageObserver) {
    window.premiereCalendarLazyImageObserver = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) {
            continue;
          }

          loadImage(entry.target);
          window.premiereCalendarLazyImageObserver.unobserve(entry.target);
        }
      },
      { rootMargin: "900px 0px" });
  }

  for (const image of images) {
    if (image.dataset.lazyObserved === "true" || image.dataset.lazyLoaded === "true") {
      continue;
    }

    image.dataset.lazyObserved = "true";
    window.premiereCalendarLazyImageObserver.observe(image);
  }
}

if (window.premiereCalendarDomObserver) {
  window.premiereCalendarDomObserver.register(observeLazyImages);
} else {
  observeLazyImages([document]);
  document.addEventListener("DOMContentLoaded", () => observeLazyImages([document]));
}

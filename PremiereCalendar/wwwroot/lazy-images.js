const lazyImageSelector = "img[data-lazy-image][data-lazy-src]";
const observedLazyImages = new Set();

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
  if (!source || (image.dataset.lazyLoaded === "true" && image.dataset.lazyLoadedSrc === source)) {
    return;
  }

  image.dataset.lazyLoaded = "true";
  image.dataset.lazyLoadedSrc = source;
  image.src = source;
}

function resetLazyImage(image) {
  if (window.premiereCalendarLazyImageObserver && observedLazyImages.has(image)) {
    window.premiereCalendarLazyImageObserver.unobserve(image);
    observedLazyImages.delete(image);
  }

  image.dataset.lazyLoaded = "false";
  image.dataset.lazyLoadedSrc = "";
  image.dataset.lazyObserved = "false";
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
          observedLazyImages.delete(entry.target);
        }
      },
      { rootMargin: "900px 0px" });
  }

  for (const image of images) {
    if (image.dataset.lazyLoaded === "true" && image.dataset.lazyLoadedSrc !== image.dataset.lazySrc) {
      resetLazyImage(image);
    }

    if (image.dataset.lazyObserved === "true" || image.dataset.lazyLoaded === "true") {
      continue;
    }

    image.dataset.lazyObserved = "true";
    window.premiereCalendarLazyImageObserver.observe(image);
    observedLazyImages.add(image);
  }
}

function cleanupObservedLazyImage(image) {
  if (!observedLazyImages.has(image)) {
    return;
  }

  window.premiereCalendarLazyImageObserver.unobserve(image);
  observedLazyImages.delete(image);
}

function cleanupDetachedLazyImages(roots = []) {
  if (!window.premiereCalendarLazyImageObserver) {
    return;
  }

  if (roots.length > 0) {
    for (const root of roots) {
      if (!(root instanceof Element)) {
        continue;
      }

      if (root.matches(lazyImageSelector)) {
        cleanupObservedLazyImage(root);
      }

      for (const image of root.querySelectorAll(lazyImageSelector)) {
        cleanupObservedLazyImage(image);
      }
    }

    return;
  }

  for (const image of Array.from(observedLazyImages)) {
    if (document.documentElement.contains(image)) {
      continue;
    }

    cleanupObservedLazyImage(image);
  }
}

if (window.premiereCalendarDomObserver) {
  window.premiereCalendarDomObserver.register(observeLazyImages);
  window.premiereCalendarDomObserver.registerCleanup(cleanupDetachedLazyImages);
} else {
  observeLazyImages([document]);
  document.addEventListener("DOMContentLoaded", () => observeLazyImages([document]));
}

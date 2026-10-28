// The size class comes from the URL hash: poster.html#story → <html class="story">. Defaults to square.
document.documentElement.className = location.hash.slice(1) || "square";

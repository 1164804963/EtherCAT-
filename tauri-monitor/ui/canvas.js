"use strict";

function contextFor(canvas) {
  const { width, height } = canvas.getBoundingClientRect();
  if (width <= 0 || height <= 0) return null;
  const dpr = window.devicePixelRatio || 1;
  const pixelsWide = Math.max(1, Math.round(width * dpr));
  const pixelsHigh = Math.max(1, Math.round(height * dpr));
  if (canvas.width !== pixelsWide) canvas.width = pixelsWide;
  if (canvas.height !== pixelsHigh) canvas.height = pixelsHigh;
  const ctx = canvas.getContext("2d");
  ctx.setTransform(pixelsWide / width, 0, 0, pixelsHigh / height, 0, 0);
  ctx.clearRect(0, 0, width, height);
  return { ctx, width, height };
}

const heatPalette = ["#263b56", "#2a5571", "#28677b", "#237b81", "#1d8c80", "#239f83", "#3ab185", "#66c28b"];

function drawHeat(canvas, values, scale) {
  const surface = contextFor(canvas);
  if (!surface) return;
  const { ctx, width, height } = surface;
  const columns = 8, gap = 4;
  const rows = values.length / columns;
  const w = (width - (columns - 1) * gap) / columns;
  const h = (height - (rows - 1) * gap) / rows;
  if (w <= 0 || h <= 0) return;
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.font = '12px "Segoe UI", sans-serif';
  values.forEach((value, index) => {
    const x = (index % columns) * (w + gap);
    const y = Math.floor(index / columns) * (h + gap);
    const color = Math.min(heatPalette.length - 1, Math.floor(value / scale * heatPalette.length));
    ctx.fillStyle = heatPalette[color];
    ctx.fillRect(x, y, w, h);
    ctx.fillStyle = color >= 4 ? "#081316" : "#f3fafb";
    ctx.fillText(String(value), x + w / 2, y + h / 2, w - 2);
  });
}

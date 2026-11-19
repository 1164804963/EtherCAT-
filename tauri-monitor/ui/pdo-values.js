"use strict";

globalThis.PdoValues = (() => {
  function bytes(image, field) {
    if (!image || field.offset + field.bits > image.length * 8 || field.bits < 1) return null;
    if (field.offset % 8 === 0 && field.bits % 8 === 0) {
      return Uint8Array.from(image.slice(field.offset / 8, (field.offset + field.bits) / 8));
    }
    const result = new Uint8Array(Math.ceil(field.bits / 8));
    for (let bit = 0; bit < field.bits; bit++) {
      const source = field.offset + bit;
      if ((image[source >> 3] >> (source & 7)) & 1) result[bit >> 3] |= 1 << (bit & 7);
    }
    return result;
  }

  const hex = value => value ? Array.from(value, octet => octet.toString(16).toUpperCase().padStart(2, "0")).join(" ") : "—";
  const signed = /^(?:SINT|INT|DINT|LINT|INTEGER(?:8|16|32|64))$/;
  const unsigned = /^(?:UINT\d*|UNSIGNED\d*|USINT|UDINT|ULINT|BYTE|WORD|DWORD|LWORD|BOOL|BOOLEAN)$/;

  function decode(value, kind, bits) {
    if (!value || bits < 1 || value.length !== Math.ceil(bits / 8)) return hex(value);
    if (kind === "REAL" && bits === 32) return String(new DataView(value.buffer).getFloat32(0, true));
    if (kind === "LREAL" && bits === 64) return String(new DataView(value.buffer).getFloat64(0, true));
    if (bits > 64 || (!signed.test(kind) && !unsigned.test(kind))) return hex(value);
    let word = 0n;
    for (let i = value.length - 1; i >= 0; i--) word = (word << 8n) | BigInt(value[i]);
    return (signed.test(kind) ? BigInt.asIntN(bits, word) : BigInt.asUintN(bits, word)).toString();
  }

  function read(image, field) {
    if (!field) return "—";
    return decode(bytes(image, field), field.kind, field.bits);
  }

  return { bytes, hex, decode, read };
})();

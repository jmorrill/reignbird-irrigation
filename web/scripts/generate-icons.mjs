// Renders every app icon from public/icon.svg — `npm run icons`.
//
// The outputs are committed, so a normal build never runs this. Re-run it after
// editing the source SVG.
//
// Every icon here is deliberately full-bleed, with no transparent margin. That
// matters more than it looks:
//
//   - A `maskable` icon is cropped by the launcher to a circle or squircle of its
//     own choosing. Transparent padding does not protect the artwork, it just
//     becomes a visible gap around a shrunken icon.
//   - iOS composites the home-screen icon onto black, so transparency there reads
//     as black corners rather than as nothing.
//
// What keeps the mark safe under an aggressive mask is not padding around the
// canvas but where the mark sits inside it. The drop occupies the middle ~62% of
// the source, comfortably inside the 80% safe zone, so the same full-bleed render
// serves both `any` and `maskable`.

import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

import pngToIco from 'png-to-ico';
import sharp from 'sharp';

const publicDir = join(dirname(fileURLToPath(import.meta.url)), '..', 'public');
const source = join(publicDir, 'icon.svg');

/** Density high enough that the 512 render is sampled from a larger raster, not upscaled. */
const render = (size) => sharp(source, { density: 512 }).resize(size, size).png({ compressionLevel: 9 });

const targets = [
  ['pwa-64x64.png', 64],
  ['pwa-192x192.png', 192],
  ['pwa-512x512.png', 512],
  // Same pixels as pwa-512, named separately because the manifest declares it with
  // purpose "maskable" and some tooling insists the two be distinct files.
  ['maskable-icon-512x512.png', 512],
  ['apple-touch-icon-180x180.png', 180],
];

await mkdir(publicDir, { recursive: true });

for (const [name, size] of targets) {
  await render(size).toFile(join(publicDir, name));
  console.log(`  ${name}`);
}

// Legacy favicon, for the browsers that ask for /favicon.ico regardless of what
// index.html declares.
const ico = await pngToIco([
  await render(16).toBuffer(),
  await render(32).toBuffer(),
  await render(48).toBuffer(),
]);
await writeFile(join(publicDir, 'favicon.ico'), ico);
console.log('  favicon.ico');

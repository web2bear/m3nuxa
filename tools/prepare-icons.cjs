// Run with Node.js and sharp available through NODE_PATH. Sources and license are kept with the PNGs.
const fs = require('node:fs/promises');
const path = require('node:path');
const sharp = require('sharp');
const root = path.resolve(__dirname, '../assets/icon-packs/Fluent');
const base = 'https://raw.githubusercontent.com/microsoft/fluentui-system-icons/main/';
const names = ['Folder', 'Document', 'Settings', 'Apps', 'Globe', 'Code', 'Mail', 'Calendar', 'Camera', 'Image', 'Music Note 2', 'Video', 'Games', 'Search', 'Save', 'Delete', 'Copy', 'Clipboard', 'Home'];
async function download(url) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${response.status}: ${url}`);
  return response.text();
}
(async () => {
  await fs.mkdir(root, { recursive: true });
  await fs.writeFile(path.join(root, 'LICENSE.txt'), await download(base + 'LICENSE'));
  const sources = [];
  for (const name of names) for (const style of ['regular', 'filled']) {
    const slug = name.toLowerCase().replaceAll(' ', '_');
    const url = base + `assets/${encodeURIComponent(name)}/SVG/ic_fluent_${slug}_24_${style}.svg`;
    const svg = await download(url);
    const folder = path.join(root, style);
    await fs.mkdir(folder, { recursive: true });
    await fs.writeFile(path.join(folder, slug + '.svg'), svg);
    await sharp(Buffer.from(svg.replaceAll('#212121', '#60a5fa'))).resize(48, 48).png().toFile(path.join(folder, slug + '.png'));
    sources.push({ name, style, url });
  }
  await fs.writeFile(path.join(root, 'sources.json'), JSON.stringify({ repository: 'https://github.com/microsoft/fluentui-system-icons', modification: 'PNG, 48px, blue #60a5fa for light and dark surfaces', icons: sources }, null, 2));
  console.log(`Prepared ${sources.length} Fluent icons`);
})().catch(error => { console.error(error); process.exitCode = 1; });


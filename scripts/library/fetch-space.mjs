// Downloads the space part of the drawing library from NASA's NASA-3D-Resources repository (exact files only,
// never a whole repo) and writes the library manifest the drawing page and the prompt are checked against.
//
// Usage: node scripts/library/fetch-space.mjs       (second run downloads nothing)
//
// NASA media are free to use without a licence fee; they must not imply NASA's endorsement.

import { createWriteStream } from 'node:fs';
import fs from 'node:fs/promises';
import https from 'node:https';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const libraryFolder = path.join(root, 'windows', 'src', 'OpenClicky.Windows', 'Assets', 'DrawStage', 'library');
const spaceFolder = path.join(libraryFolder, 'space');
const manifestPath = path.join(libraryFolder, 'manifest.json');

const RAW = 'https://raw.githubusercontent.com/nasa/NASA-3D-Resources/master/';
const TEXTURES = 'Images and Textures';
const MODELS = '3D Models';

const raw = (folder, file) => RAW + folder.split('/').map(encodeURIComponent).join('/') + '/' + encodeURIComponent(file);

const texture = (id, folder, file) => ({
  id,
  file: `space/${id}.jpg`,
  url: raw(`${TEXTURES}/${folder}`, file),
});

// Only the files listed here are downloaded. Mercury, Uranus and the Sun have no texture folder of that name in the
// repository, so they are left out of the library for now.
const textures = [
  texture('earth', 'Earth (A)', 'Earth (A).jpg'),
  texture('moon', 'Moon', 'Moon.jpg'),
  texture('mars', 'Mars', 'Mars.jpg'),
  texture('jupiter', 'Jupiter', 'Jupiter.jpg'),
  texture('saturn', 'Saturn', 'Saturn.jpg'),
  texture('neptune', 'Neptune', 'Neptune.jpg'),
  texture('pluto', 'Pluto', 'Pluto.jpg'),
  texture('venus', 'Venus', 'Venus.jpg'),
  texture('europa', 'Jupiter - Europa', 'Jupiter - Europa.jpg'),
  texture('io', 'Jupiter - Io (A)', 'Jupiter - Io (A).jpg'),
  texture('titan', 'Saturn - Titan', 'Saturn - Titan.jpg'),
  texture('phobos', 'Mars - Phobos', 'Mars - Phobos.jpg'),
  // A ground patch for the Moon, kept for later ground work; it is not a sphere texture.
  { id: 'moon-ground', file: 'space/moon-ground.jpg', url: raw(`${TEXTURES}/Moon - Lunar Terrain`, 'Moon - Lunar Terrain.jpg') },
];

const model = (id, folder, file) => ({ id, file: `space/${id}.glb`, url: raw(`${MODELS}/${folder}`, file) });

// Curiosity Rover (MSL) only ships .blend files, so it is not in the library yet (a later part converts it).
const models = [
  model('iss', 'International Space Station (ISS) (A)', 'International Space Station (ISS) (A).glb'),
  model('hubble', 'Hubble Space Telescope (A)', 'Hubble Space Telescope (A).glb'),
  model('jwst', 'James Webb Space Telescope (A)', 'James Webb Space Telescope (A).glb'),
  model('ingenuity', 'Ingenuity Mars Helicopter', 'Ingenuity Mars Helicopter.glb'),
  model('apollo-lunar-module', 'Apollo Lunar Module', 'Apollo Lunar Module.glb'),
  model('astronaut', 'Astronaut', 'Astronaut.glb'),
];

const CREDIT = 'NASA';
const LICENCE = 'NASA media (no fee; no endorsement)';

const planets = [
  { id: 'earth', names: ['earth', 'terra'], texture: 'space/earth.jpg', rings: false, radiusKm: 6371 },
  { id: 'moon', names: ['moon', 'the moon', 'luna'], texture: 'space/moon.jpg', rings: false, radiusKm: 1737 },
  { id: 'mars', names: ['mars', 'the red planet'], texture: 'space/mars.jpg', rings: false, radiusKm: 3390 },
  { id: 'jupiter', names: ['jupiter'], texture: 'space/jupiter.jpg', rings: false, radiusKm: 69911 },
  { id: 'saturn', names: ['saturn'], texture: 'space/saturn.jpg', rings: true, radiusKm: 58232 },
  { id: 'neptune', names: ['neptune'], texture: 'space/neptune.jpg', rings: false, radiusKm: 24622 },
  { id: 'pluto', names: ['pluto'], texture: 'space/pluto.jpg', rings: false, radiusKm: 1188 },
  { id: 'venus', names: ['venus'], texture: 'space/venus.jpg', rings: false, radiusKm: 6052 },
  { id: 'europa', names: ['europa', "jupiter's moon europa"], texture: 'space/europa.jpg', rings: false, radiusKm: 1561 },
  { id: 'io', names: ['io', "jupiter's moon io"], texture: 'space/io.jpg', rings: false, radiusKm: 1822 },
  { id: 'titan', names: ['titan', "saturn's moon titan"], texture: 'space/titan.jpg', rings: false, radiusKm: 2575 },
  { id: 'phobos', names: ['phobos', "mars' moon phobos"], texture: 'space/phobos.jpg', rings: false, radiusKm: 11 },
].map((planet) => {
  const found = textures.find((one) => one.id === planet.id);
  return {
    ...planet,
    credit: CREDIT,
    licence: LICENCE,
    source: found.url,
  };
});

const modelNames = {
  iss: ['iss', 'international space station', 'space station'],
  hubble: ['hubble', 'hubble space telescope'],
  jwst: ['jwst', 'james webb', 'james webb space telescope', 'webb telescope'],
  ingenuity: ['ingenuity', 'mars helicopter', 'ingenuity mars helicopter'],
  'apollo-lunar-module': ['apollo lunar module', 'lunar module', 'lem', 'apollo lander'],
  astronaut: ['astronaut', 'space suit', 'spacesuit'],
};

const manifest = {
  models: models.map((one) => ({
    id: one.id,
    names: modelNames[one.id],
    kind: 'model',
    file: one.file,
    parts: [],
    credit: CREDIT,
    licence: LICENCE,
    source: one.url,
  })),
  planets,
};

function download(url, destination) {
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { 'User-Agent': 'OpenClicky-draw-library-fetch' } }, (response) => {
      if (response.statusCode >= 300 && response.statusCode < 400 && response.headers.location) {
        response.resume();
        resolve(download(new URL(response.headers.location, url).href, destination));
        return;
      }
      if (response.statusCode !== 200) {
        response.resume();
        reject(new Error(`HTTP ${response.statusCode} for ${url}`));
        return;
      }
      const temporary = destination + '.part';
      const file = createWriteStream(temporary);
      response.pipe(file);
      file.on('finish', () => file.close(() => fs.rename(temporary, destination).then(resolve, reject)));
      file.on('error', reject);
      response.on('error', reject);
    }).on('error', reject);
  });
}

async function ensureFile({ file, url }) {
  const destination = path.join(libraryFolder, file);
  await fs.mkdir(path.dirname(destination), { recursive: true });
  try {
    const info = await fs.stat(destination);
    if (info.size > 0) {
      console.log(`kept   ${file} (${info.size.toLocaleString()} bytes)`);
      return { bytes: info.size, kept: true };
    }
  } catch (_) { /* not there yet */ }
  await download(url, destination);
  const info = await fs.stat(destination);
  console.log(`saved  ${file} (${info.size.toLocaleString()} bytes)`);
  return { bytes: info.size, kept: false };
}

async function main() {
  await fs.mkdir(spaceFolder, { recursive: true });
  const results = [];
  for (const one of [...textures, ...models]) results.push(await ensureFile(one));
  const total = results.reduce((sum, one) => sum + one.bytes, 0);
  const saved = results.filter((one) => !one.kept).length;
  console.log(`\n${saved} file(s) downloaded, ${results.length - saved} kept; ${(total / (1024 * 1024)).toFixed(2)} MB in ${path.relative(root, spaceFolder)}`);
  const big = results.filter((one) => one.bytes > 4 * 1024 * 1024);
  for (const one of big) console.log(`note: ${one.bytes.toLocaleString()} bytes is over 4 MB (kept as it is; no resize tools assumed)`);

  await fs.writeFile(manifestPath, JSON.stringify(manifest, null, 2) + '\n', 'utf8');
  console.log(`wrote  ${path.relative(root, manifestPath)} (${manifest.models.length} models, ${manifest.planets.length} planets)`);
}

main().catch((error) => {
  console.error(error.message || error);
  process.exitCode = 1;
});

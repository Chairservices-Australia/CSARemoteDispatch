const initialZoom = 20;
const earthCircumference = 40e6;
const metersToDegrees = 360 / earthCircumference;

/////////////////////
// server requests

// Every endpoint that can fail answers with a JSON {error} body, but a few
// still reply with no body at all - a 204, an auth challenge, or a dropped
// connection. Handing an empty body to JSON.parse is what produced
// "unexpected end of data at line 1 column 1", which told the user nothing
// about what had actually gone wrong. Read the body as text first, and treat
// "empty" and "not JSON" as their own outcomes.
function fetchJson(url, options) {
  return fetch(url, options).then(response =>
    response.text().then(text => {
      let data = null;
      if (text) {
        try {
          data = JSON.parse(text);
        } catch (e) {
          throw new Error(`The server sent a malformed reply (${response.status}).`);
        }
      }
      if (!response.ok)
        throw new Error((data && data.error) || `Request failed (${response.status}).`);
      if (data === null)
        throw new Error('The server sent an empty reply.');
      return data;
    }));
}

/////////////////////
// map

const canvasRenderer = L.canvas();
const mapBounds = [[0, 0], [0.15, 0.15]];
const maxBounds = [[-0.02, -0.02], [0.17, 0.17]];
const map = L.map('map', {
  minZoom: 13,
  maxBounds: maxBounds,
  tap: false,
  zoomControl: false,
})
.fitBounds(mapBounds);
L.control.scale().addTo(map);
const zoomHome = new L.Control.ZoomHome({
  position: 'topleft',
  zoomInText: '<i class="fas fa-search-plus"></i>',
  zoomHomeText: '<i class="fas fa-user"></i>',
  zoomHomeTitle: 'Zoom to player(s)',
  zoomOutText: '<i class="fas fa-search-minus"></i>',
}).addTo(map);

let markerToFollow;
let followMode = null;   // 'player' | 'train' | null
map.addEventListener('mousedown', stopFollowing);
map.on('drag', () => {
    map.fitBounds(map.getBounds());
});
map.on('zoomanim', () => {
    map.fitBounds(map.getBounds());
});

function setMarkerToFollow(marker) {
  markerToFollow = marker;
  map.panTo(marker.getBounds().getCenter());
}

function stopFollowing() {
  markerToFollow = undefined;
  followMode = null;
  updateFollowButtons();
}

function zoomToAllPlayers() {
  const bounds = new L.LatLngBounds();
  playerMarkers.forEach(marker => bounds.extend(marker.getBounds()));
  map.fitBounds(bounds, { maxZoom: initialZoom });
}

map.addEventListener('zoomhome', () => {
  stopFollowing();
  zoomToAllPlayers();
});

/////////////////////
// follow controls

// Following used to require clicking the marker itself, which is fiddly on a
// moving train and easy to lose. These give it a fixed target you can hit.

function updateFollowButtons() {
  for (const button of document.querySelectorAll('.followControlButton'))
    button.classList.toggle('active', followMode === button.dataset.followMode);
}

function followPlayer() {
  // Follow the only player in single player; otherwise the first reported.
  const marker = playerMarkers.values().next().value;
  if (!marker) {
    followMode = null;
    updateFollowButtons();
    return;
  }
  markerToFollow = marker;
  followMode = 'player';
  map.panTo(marker.getBounds().getCenter());
  updateFollowButtons();
}

// Follow whichever train is selected in the routing tab, so the dispatcher can
// watch the consist it is routing without hunting for it on the map.
function followSelectedTrain() {
  const selected = Array.from(allCarData.entries())
    .find(([_, carData]) => carData.guid === routeTrainSelect.value);
  if (!selected) {
    followMode = null;
    updateFollowButtons();
    return;
  }
  const trainsetId = selected[1].trainsetId;
  let target = null;
  for (const [carId, carData] of allCarData) {
    if (carData.trainsetId !== trainsetId)
      continue;
    target = carId;
    if (carId.startsWith('L-'))
      break;
  }
  const marker = target && carMarkers.get(target);
  if (!marker) {
    followMode = null;
    updateFollowButtons();
    return;
  }
  markerToFollow = marker;
  followMode = 'train';
  map.panTo(marker.getBounds().getCenter());
  updateFollowButtons();
}

const FollowControl = L.Control.extend({
  options: { position: 'topleft' },
  onAdd: function () {
    const container = L.DomUtil.create('div', 'leaflet-bar followControl');
    const buttons = [
      { mode: 'player', icon: 'fa-street-view', title: 'Follow me', handler: followPlayer },
      { mode: 'train', icon: 'fa-train', title: 'Follow selected train', handler: followSelectedTrain },
    ];
    for (const spec of buttons) {
      const link = L.DomUtil.create('a', 'followControlButton', container);
      link.href = '#';
      link.title = spec.title;
      link.dataset.followMode = spec.mode;
      link.innerHTML = `<i class="fas ${spec.icon}"></i>`;
      L.DomEvent.on(link, 'click', L.DomEvent.stop);
      L.DomEvent.on(link, 'click', () => {
        if (followMode === spec.mode)
          stopFollowing();
        else
          spec.handler();
      });
    }
    L.DomEvent.disableClickPropagation(container);
    return container;
  },
});

map.addControl(new FollowControl());

/////////////////////
// settings

// The theme class goes on <body> as well as the map, so the sidebar, tables and
// controls can be themed rather than only the map backdrop.
function applyTheme(theme) {
  const dark = theme === 'dark';
  document.body.classList.toggle('theme-dark', dark);
  document.body.classList.toggle('theme-light', !dark);
  document.getElementById('map').classList.toggle('dark', dark);
}

document.getElementById('themeDropdown')
  .addEventListener('input', e => applyTheme(e.target.value));

applyTheme(document.getElementById('themeDropdown').value);

function getCarColorMode() {
  return document.getElementById('carColorDropdown').value;
}

function getCarScale() {
  return Number(document.getElementById('carSizeDropdown').value) || 1;
}

// Minimum on-screen width of a car, in pixels, at scale 1. Cars are drawn at
// true geographic size, which makes them invisible specks when zoomed out, so
// below this width we stop shrinking them and hold a readable size instead.
const minCarWidthPx = 7;

function pixelsPerMeter() {
  const center = map.getCenter();
  const zoom = map.getZoom();
  const origin = map.project(center, zoom);
  const oneMeterNorth = map.project([center.lat + metersToDegrees, center.lng], zoom);
  return Math.abs(oneMeterNorth.y - origin.y);
}

// Cars are drawn end to end at their true length, so scaling length overlaps
// each car onto its neighbour by exactly the amount scaled. The two reasons for
// scaling are therefore kept apart:
//
//   zoom clamp  - applies to length and width together, and only ever enlarges.
//                 It exists so a consist stays visible when zoomed out, where
//                 the whole train is a few pixels and overlap between its cars
//                 cannot be seen anyway.
//   user scale  - applies to width only, so cars can be made bolder at working
//                 zoom without ever running into one another.
function carZoomClamp() {
  const trueWidthPx = carWidthMeters * pixelsPerMeter();
  if (!isFinite(trueWidthPx) || trueWidthPx <= 0)
    return 1;
  return Math.max(1, minCarWidthPx / trueWidthPx);
}

function getCarLengthScale() {
  return carZoomClamp();
}

function getCarWidthScale() {
  // Keep the widest setting within the footprint of the shortest vehicle while
  // still allowing every dropdown option, including Huge, to be distinct.
  return carZoomClamp() * Math.min(getCarScale(), maxCarWidthRatio * shortestCarLength / carWidthMeters);
}

// Widest a car may be drawn relative to its own length.
const maxCarWidthRatio = 0.8;
const shortestCarLength = 12;

function refreshAllCarMarkers() {
  for (const carId of carMarkers.keys())
    updateCarMarker(carId);
}

document.getElementById('carSizeDropdown')
  .addEventListener('input', refreshAllCarMarkers);

// Re-render on zoom so the clamp above is recomputed for the new scale.
map.on('zoomend', refreshAllCarMarkers);

document.getElementById('carColorDropdown')
  .addEventListener('input', () => {
    updateAllCarColors();
    updateJobListColors();
  });

/////////////////////
// sidebar

const sidebar = L.control.sidebar({ autopan: true, container: 'sidebar' }).addTo(map);

const tablesort = new Tablesort(document.getElementById('carList'));
const carListBody = document.getElementById('carListBody');

function createCarRow(carId) {
  const row = document.createElement('tr');
  row.setAttribute('id', `carList-${carId}`);
  row.classList.add('interactive');
  carListBody.append(row);
  updateCarRow(carId);
  row.addEventListener('click', _ => followCar(carId, false) );
}

function removeCarRow(carId) {
  const row = document.getElementById(`carList-${carId}`);
  if (row)
    row.remove();
}

function updateCarRow(carId) {
  const row = document.getElementById(`carList-${carId}`);
  if (!row)
    return;
  const jobId = carJobIds.has(carId) ? carJobIds.get(carId) : '';
  const destinationYardId = allJobData.has(jobId) ? allJobData.get(jobId).destinationYardId : '';
  row.innerHTML = `<td>${carId}</td><td>${jobId}</td><td>${destinationYardId}</td>`;
  tablesort.refresh();
}

/////////////////////
// jobs

const CarsPerRow = 3;
const allJobData = new Map();
const carJobIds = new Map();
const jobListBody = document.getElementById('jobListBody');

// https://www.npmjs.com/package/string-hash
function stringHash(str) {
  let hash = 5381, i = str.length;
  while(i) {
    hash = (hash * 33) ^ str.charCodeAt(--i);
  }
  return hash >>> 0;
}

// http://vrl.cs.brown.edu/color
const carColors = [
  '#52ef99', '#c95e9f', '#b1e632', '#7574f5', '#799d10', '#fd3fbe', '#2cf52b', '#d130ff', '#21a708', '#fd2b31',
  '#3eeaef', '#ffc4de', '#069668', '#f9793b', '#5884c9', '#e5d75e', '#96ccfe', '#bb8801', '#6a8b7b', '#a8777c',
];

function colorByHashing(str) {
  return carColors[stringHash(str) % carColors.length];
}

function colorForJobDestination(jobId) {
  const jobData = allJobData.get(jobId);
  if (!jobData)
    return 'gray';
  return colorForYardId(jobData.destinationYardId);
}

function colorForJobType(jobId) {
  const segments = jobId.split('-');
  if (segments.length == 2)
    return 'cornflowerblue';
  const jobType = segments[1];
  switch (jobType) {
  case 'FH': return 'lightgreen';
  case 'LH': return 'khaki';
  case 'PC':
  case 'PE': return 'cornflowerblue';
  case 'PR': return 'mediumpurple';
  case 'SL':
  case 'SU': return 'lightcoral';
  }
}

function colorForJobId(jobId) {
  switch (getCarColorMode()) {
    case 'jobId': return colorByHashing(jobId);
    case 'carType':
    case 'jobType': return colorForJobType(jobId);
    case 'destination': return colorForJobDestination(jobId);
  }
}

function yardIdForTrack(trackId) {
  return trackId.split('-')[0];
}

function jobMatchesFilter(jobId, jobData) {
    const testText = document.getElementById('jobSearchText').value.toUpperCase();
    const activeOnly = document.getElementById('jobActiveOnly').checked;
  function taskFields(task) { return [task.startTrack, task.destinationTrack].concat(task.cars); }
  const fields = [jobId].concat(jobData.tasks.flatMap(taskFields));
  return fields.some(field => field.includes(testText)) && (!activeOnly || jobData.isActive);
}

function jobElem(jobId, jobData) {
  function replaceHyphens(s) { return s.replaceAll('-', '\u2011'); }

  const tbody = document.createElement('tbody');
  tbody.setAttribute('id', `jobList-${jobId}`);

  let row = document.createElement('tr');
  const jobIdCell = document.createElement('th'); 
  jobIdCell.setAttribute('colspan', CarsPerRow);
  jobIdCell.classList.add("jobList-jobHeader");
  jobIdCell.style.background = colorForJobId(jobId);
  jobIdCell.textContent = jobId;

  jobLicensesDiv = document.createElement('div');
  jobLicensesDiv.classList.add('jobList-licenses');
  for (const license of jobData.requiredLicenses) {
      jobLicensesDiv.innerHTML += `<span class="jobList-license"><div class="jobList-licenseBackground"></div><img src="res/licenses.${license}.png" title="${license}"></span>`;
  }
  jobIdCell.appendChild(jobLicensesDiv);

  row.appendChild(jobIdCell);
  tbody.appendChild(row);

  row = document.createElement('tr');
  jobMassCell = document.createElement('th');
  jobMassCell.textContent = `${jobData.mass.toFixed(0)} t`;
  jobLengthCell = document.createElement('th');
  jobLengthCell.textContent = `${jobData.length.toFixed(0)} m`;
  jobPaymentCell = document.createElement('th');
  jobPaymentCell.textContent =
    new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', maximumFractionDigits: 0 })
    .format(jobData.basePayment);
  row.append(jobMassCell, jobLengthCell, jobPaymentCell);
  tbody.appendChild(row);

  jobData.tasks.forEach(task => {
    row = document.createElement('tr');
    const startTrackCell = document.createElement('th');
    startTrackCell.classList.add('interactive');
    startTrackCell.textContent = replaceHyphens(task.startTrack);
    startTrackCell.style.background = colorForYardId(yardIdForTrack(task.startTrack));
    startTrackCell.addEventListener('click', () => scrollToTrack(task.startTrack));
    row.appendChild(startTrackCell);

    const arrowCell = document.createElement('th');
    arrowCell.textContent = "\u279C";
    arrowCell.classList.add('jobList-trackSeparator');
    row.appendChild(arrowCell);

    const destinationTrackCell = document.createElement('th');
    destinationTrackCell.classList.add('interactive');
    destinationTrackCell.textContent = replaceHyphens(task.destinationTrack);
    destinationTrackCell.style.background = colorForYardId(yardIdForTrack(task.destinationTrack));
    destinationTrackCell.addEventListener('click', () => scrollToTrack(task.destinationTrack));
    row.appendChild(destinationTrackCell);

    for (let carIndex = 0; carIndex < task.cars.length; carIndex++) {
      if (carIndex % CarsPerRow == 0) {
        tbody.appendChild(row);
        row = document.createElement('tr');
      }
      const carId = task.cars[carIndex];
      const carCell = document.createElement('td');
      carCell.classList.add(`jobList-carCell-${carId}`);
      carCell.classList.add('interactive');
      carCell.textContent = carId;
      carCell.addEventListener('click', () => followCar(carId, false));
      row.appendChild(carCell);
    }
    if (row.children.length < CarsPerRow)
      // add filler cells
      for (let i = 0; i < CarsPerRow - (task.cars.length % CarsPerRow); i++)
        row.appendChild(document.createElement('td'));
    tbody.appendChild(row);
  });

  return tbody;
}

function updateCarJobs() {
  carJobIds.clear();
  allJobData.forEach((jobData, jobId) => {
    jobData.tasks.forEach(task => {
      task.cars.forEach(carId => {
        carJobIds.set(carId, jobId);
      });
    })
  });
  for ([carId, _] of allCarData) {
    updateCarRow(carId);
    updateCarMarker(carId);
  }
}

function updateJobListColors() {
  for (const elem of jobListBody.querySelectorAll('th.jobList-jobHeader')) {
    elem.style.background = colorForJobId(elem.textContent);
  }
}

function updateJobList() {
  for (const elem of Array.from(jobListBody.childNodes))
    elem.remove();
  const sortedJobs = Array.from(allJobData.entries()).sort((a, b) => a[0].localeCompare(b[0]));
  sortedJobs
    .filter(([jobId, jobData]) => jobMatchesFilter(jobId, jobData))
    .forEach(([jobId, jobData]) => jobListBody.appendChild(jobElem(jobId, jobData)));
}

function updateAllJobs(jobs) {
  allJobData.clear();
  Object.entries(jobs).forEach(([jobId, jobData]) => allJobData.set(jobId, jobData));
  updateJobList();
  updateCarJobs();
}

let jobSearchTimeoutId;
function queueJobUpdate() {
    if (jobSearchTimeoutId)
        clearTimeout(jobSearchTimeoutId);
    jobSearchTimeoutId = setTimeout(updateJobList, 100);
}
document.getElementById('jobSearchText').addEventListener('input', e => {
    queueJobUpdate();
});
document.getElementById('jobActiveOnly').addEventListener('change', e => {
    queueJobUpdate();
})

/////////////////////
// track

const trackPolyLines = new Map();

function colorForYardId(yardId) {
  switch (yardId) {
    case 'CME': return '#686868';
    case 'CMS': return '#4e554e';
    case 'CP': return '#583d3d';
    case 'CS': return '#97adc2';
    case 'CW': return '#a7a7a7';
    case 'FF': return '#77a6e3';
    case 'FM': return '#ddaa4d';
    case 'FRC': return '#92b66a';
    case 'FRS': return '#609161';
    case 'GF': return '#c97fa2';
    case 'HB': return '#816c94';
    case 'HMB': return '#816c94';
    case 'IME': return '#b66861';
    case 'IMW': return '#9a5847';
    case 'MB': return '#988c5f';
    case 'MF': return '#dc885b';
    case 'MFMB': return '#dc885b';
    case 'OR': return '#935478';
    case 'OWC': return '#555a62';
    case 'OWN': return '#625d55';
    case 'SM': return '#7b8394';
    case 'SW': return '#cda888';
  }
}

function createTrackLabel(trackId, position, angle) {
  const size = 0.0002;
  const bounds = [[position[0] - size, position[1] - size], [position[0] + size, position[1] + size]];
  const rotation = `rotate(${-angle})`;

  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('id', trackId)
  svg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  svg.setAttribute('viewBox', '-50 -10 100 20');
  svg.innerHTML =
    `<text text-anchor="middle" dominant-baseline="central" transform="${rotation}" font-family="Arial" font-weight="bold" fill="steelblue" stroke="black" stroke-width="0.25px">${trackId.slice(trackId.indexOf('-') + 1)}</text>`;
  L.svgOverlay(svg, bounds, { renderer: canvasRenderer })
  .addTo(map)
  .setZIndex(1000);
}

function pointDistance(p1, p2) {
  const d0 = p1[0] - p2[0];
  const d1 = p1[1] - p2[1];
  return Math.sqrt(d0 * d0 + d1 * d1);
}

function pointLerp(p1, p2, a) {
  return [
    (p2[0] - p1[0]) * a + p1[0],
    (p2[1] - p1[1]) * a + p1[1]
  ];
}

function createLocation(start, end, mid, a) {
  return [
    (end[0] - start[0]) * a + mid[0],
    (end[1] - start[1]) * a + mid[1]
  ];
}

function createTrackLabels(trackId, coords) {
  const length = pointDistance(coords[0], coords[coords.length - 1]);
  const midIndex = Math.floor(coords.length / 2); 
  const beforeMid = (midIndex % 2 == 1) ? coords[midIndex] : coords[midIndex - 1];
  const mid = (midIndex % 2 == 1) ? coords[midIndex] : pointLerp(coords[midIndex - 1], coords[midIndex], 0.5);
  const afterMid = (midIndex % 2 == 1) ? coords[midIndex + 1] : coords[midIndex];
  const midGap = pointDistance(beforeMid, afterMid);

  const angle = ((Math.atan2(afterMid[0] - beforeMid[0], afterMid[1] - beforeMid[1]) * 180 / Math.PI) + 270) % 180 - 90;

  if (coords.length > 5) {
    createTrackLabel(trackId, createLocation(beforeMid, afterMid, mid, length / midGap *  0.3), angle);
    createTrackLabel(trackId, createLocation(beforeMid, afterMid, mid, length / midGap * -0.3), angle);
  } else {
    createTrackLabel(trackId, mid, angle);
  }
}

const tracksReady = fetchJson(new URL('/track', location))
.then(tracks => {
  Object.entries(tracks).forEach(([trackId, coords]) => {
    const isSiding = !trackId.includes('#');
    const polyline = L.polyline(coords, {
      color: isSiding ? 'slategray' : 'lightsteelblue',
      interactive: false,
      renderer: canvasRenderer,
    }).addTo(map);
    trackPolyLines.set(trackId, polyline);
    if (isSiding)
      createTrackLabels(trackId, coords)
  });
});

/////////////////////
// stations

const stationMarkers = new Map();

// WCAG relative luminance, so light yards get dark text and vice versa.
function textColorFor(hexColor) {
  const match = /^#?([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(hexColor || '');
  if (!match)
    return '#fff';
  const [r, g, b] = [1, 2, 3]
    .map(i => parseInt(match[i], 16) / 255)
    .map(c => c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b > 0.35 ? '#000' : '#fff';
}

function showStationLabels() {
  return document.getElementById('stationLabelsCheckbox').checked;
}

function createStationLabel(station) {
  const color = station.color || '#888';
  const icon = L.divIcon({
    className: 'stationLabel',
    iconSize: null,
    html:
      `<span class="stationLabel-pill" style="background:${color};color:${textColorFor(color)}">` +
        `<span class="stationLabel-yardId">${station.yardId}</span>` +
        `<span class="stationLabel-name">${station.name}</span>` +
      '</span>',
  });
  return L.marker(station.position, { icon: icon, interactive: false, zIndexOffset: 900 });
}

function applyStationLabelVisibility() {
  const visible = showStationLabels();
  for (const marker of stationMarkers.values()) {
    if (visible)
      marker.addTo(map);
    else
      marker.remove();
  }
}

const stationsReady = fetchJson(new URL('/station', location))
  .then(stations => {
    const sorted = [...stations].sort((a, b) => a.name.localeCompare(b.name));
    for (const station of sorted) {
      stationMarkers.set(station.yardId, createStationLabel(station));
      stationTracks.set(station.yardId, station.tracks || []);
    }
    applyStationLabelVisibility();
    fillSelect(
      routeStationSelect,
      sorted.map(station => [station.yardId, `${station.yardId} - ${station.name}`]),
      false);
    updateRouteTrackList();
    refreshRoutes();
    refreshCurrentTrain();
    setInterval(refreshCurrentTrain, 2000);
  });

document.getElementById('stationLabelsCheckbox')
  .addEventListener('input', applyStationLabelVisibility);

/////////////////////
// routing

const stationTracks = new Map();   // yardId -> [trackId]
const routeTrainSelect = document.getElementById('routeTrainSelect');
const routeStationSelect = document.getElementById('routeStationSelect');
const routeTrackSelect = document.getElementById('routeTrackSelect');
const routeMessage = document.getElementById('routeMessage');
const routeListBody = document.getElementById('routeListBody');

function fillSelect(select, entries, keepValue) {
  const previous = keepValue ? select.value : null;
  select.innerHTML = '';
  for (const [value, label] of entries) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = label;
    select.appendChild(option);
  }
  if (previous !== null && entries.some(([value]) => value === previous))
    select.value = previous;
}

// Every locomotive is listed, not just remotely controllable ones: a dispatcher
// sets roads for any train. Routing acts on the whole consist the loco is part
// of, so unpowered consists are listed too, labelled by a car in them.
function updateRouteTrainList() {
  const entries = [];
  const seenTrainsets = new Set();

  const locos = Array.from(allCarData.entries())
    .filter(([carId, carData]) => carId.startsWith('L-') && carData.trainsetId >= 0)
    .sort(([a], [b]) => a.localeCompare(b));
  for (const [carId, carData] of locos) {
    entries.push([carData.guid, carId]);
    seenTrainsets.add(carData.trainsetId);
  }

  // Consists with no locomotive, so shunted cuts can still be given a road.
  const looseCars = new Map();
  for (const [carId, carData] of allCarData) {
    const id = carData.trainsetId;
    if (id === undefined || id < 0 || seenTrainsets.has(id))
      continue;
    if (!looseCars.has(id))
      looseCars.set(id, carId);
  }
  for (const [id, carId] of [...looseCars.entries()].sort((a, b) => a[1].localeCompare(b[1])))
    entries.push([allCarData.get(carId).guid, carId + ' (no loco)']);

  fillSelect(routeTrainSelect, entries, true);
  applyAutoSelection();
}

// Tracks are labelled with the game's display ID ("GF-D5I"), matching what jobs
// and lineside signage show, while the canonical ID stays as the value.
function updateRouteTrackList() {
  const tracks = stationTracks.get(routeStationSelect.value) || [];
  const entries = tracks
    .map(track => typeof track === 'string'
      ? [track, track]
      : [track.id, track.display || track.id])
    .sort((a, b) => a[1].localeCompare(b[1]));
  fillSelect(routeTrackSelect, entries, true);
}

routeStationSelect.addEventListener('input', updateRouteTrackList);

// Bright green over the booked road, so where a train is going reads at a
// glance without tracing junctions by eye.
// Roads are drawn in the order they were set. The index comes from the host,
// so the same road is the same colour on every player's map.
const routeColors = [
  '#00e676', '#ff9100', '#40c4ff', '#ff4081', '#ffea00',
  '#b388ff', '#ff5252', '#1de9b6', '#c6ff00', '#8c9eff',
];

function routeColor(route) {
  const index = Number.isInteger(route.colorIndex) ? route.colorIndex : 0;
  return routeColors[((index % routeColors.length) + routeColors.length) % routeColors.length];
}

function routeOrder(route) {
  return Number.isInteger(route.sequence) ? route.sequence : 0;
}
const highlightedTracks = new Set();

function baseTrackColor(trackId) {
  // Matches how the track layer is drawn: yard tracks are named, plain line
  // carries the generic '#' form.
  return trackId.includes('#') ? 'lightsteelblue' : 'slategray';
}

function applyRouteHighlight(routes) {
  // Where two roads cross they share a track, and only one colour can be drawn
  // on it. The earlier road keeps it: picking by creation order means the
  // colour is stable, rather than depending on the order the list arrived in.
  const colorByTrack = new Map();
  const orderByTrack = new Map();
  for (const route of routes) {
    if (route.status === 'Failed' || route.status === 'Cleared')
      continue;
    const color = routeColor(route);
    const order = routeOrder(route);
    for (const trackId of route.tracks || []) {
      if (orderByTrack.has(trackId) && orderByTrack.get(trackId) <= order)
        continue;
      orderByTrack.set(trackId, order);
      colorByTrack.set(trackId, color);
    }
  }

  for (const trackId of highlightedTracks) {
    if (colorByTrack.has(trackId))
      continue;
    const polyline = trackPolyLines.get(trackId);
    if (polyline)
      polyline.setStyle({ color: baseTrackColor(trackId), weight: 3 });
  }

  for (const [trackId, color] of colorByTrack) {
    const polyline = trackPolyLines.get(trackId);
    if (!polyline)
      continue;
    polyline.setStyle({ color: color, weight: 5 });
    polyline.bringToFront();
  }

  highlightedTracks.clear();
  for (const trackId of colorByTrack.keys())
    highlightedTracks.add(trackId);
}

function appendCell(row, text) {
  const cell = document.createElement('td');
  cell.textContent = text;
  row.appendChild(cell);
  return cell;
}

function renderRoutes(routes) {
  applyRouteHighlight(routes);
  routeListBody.innerHTML = '';
  for (const route of routes) {
    const row = document.createElement('tr');

    // The swatch is what ties a row to the coloured road on the map, and the
    // number says where it came in the order.
    const marker = document.createElement('td');
    const dot = document.createElement('span');
    dot.className = 'routeSwatch';
    dot.style.backgroundColor = routeColor(route);
    marker.appendChild(dot);
    marker.appendChild(document.createTextNode(String(routeOrder(route))));
    marker.title = 'Roads are numbered and coloured in the order they were set.';
    row.appendChild(marker);

    appendCell(row, route.trainsetId);
    appendCell(row, route.requestedBy || 'Local');
    appendCell(row, route.destinationTrack);
    // Built as text rather than interpolated markup: track and signal names
    // come from the world and from other mods, and a quote in one of them used
    // to break out of the title attribute.
    const status = appendCell(row, route.status);
    status.className = `routeStatus routeStatus-${route.status}`;
    status.title = route.message || '';

    const actions = document.createElement('td');
    const clear = document.createElement('button');
    clear.textContent = 'Clear';
    clear.addEventListener('click', () => clearRoute(route.id));
    actions.appendChild(clear);
    row.appendChild(actions);
    routeListBody.appendChild(row);

    // Some of these are instructions the driver has to act on - draw forward
    // past a signal and set back, or stand short of a crossing until it clears
    // - so they belong in the list, not hidden in a tooltip.
    if (route.message) {
      const note = document.createElement('tr');
      const cell = document.createElement('td');
      cell.colSpan = 6;
      cell.className = 'routeMessage'
        + (route.status === 'AwaitingReversal' ? ' routeMessage-action' : '');
      cell.textContent = route.message;
      note.appendChild(cell);
      routeListBody.appendChild(note);
    }
  }
}

function refreshRoutes() {
  return fetchJson(new URL('/route', location))
    .then(renderRoutes)
    .catch(() => {});
}

function clearRoute(routeId) {
  fetchJson(new URL(`/route/${routeId}/clear`, location), { method: 'POST', body: '' })
    .then(renderRoutes)
    .catch(error => { routeMessage.textContent = error.message || 'Could not clear the route.'; });
}

// Detect the consist the player is riding in and the job it is working, so
// being aboard and pressing Set route is enough: the train and its booked
// destination are already selected.
let autoDetectedTrainGuid = null;
let autoAppliedTrainGuid = null;      // the last one actually selected
let autoDestinationTrack = null;

function selectStationForTrack(trackDisplayId) {
  for (const [yardId, tracks] of stationTracks) {
    const match = tracks.find(track => typeof track === 'string'
      ? track === trackDisplayId
      : track.display === trackDisplayId || track.id === trackDisplayId);
    if (!match)
      continue;
    routeStationSelect.value = yardId;
    updateRouteTrackList();
    const wanted = typeof match === 'string' ? match : match.id;
    for (const option of routeTrackSelect.options) {
      if (option.value === wanted) {
        routeTrackSelect.value = wanted;
        return true;
      }
    }
    return false;
  }
  return false;
}

// Applied separately from detection: the train options are filled from car
// updates, which can arrive after the first detection poll. Setting a select to
// a value it has no option for silently does nothing, so this retries until the
// option exists rather than giving up after one attempt.
function applyAutoSelection() {
  if (autoDetectedTrainGuid === null)
    return;
  if (autoAppliedTrainGuid === autoDetectedTrainGuid)
    return;

  const wanted = autoDetectedTrainGuid;
  const hasOption = Array.from(routeTrainSelect.options).some(option => option.value === wanted);
  if (!hasOption)
    return;

  routeTrainSelect.value = wanted;
  if (autoDestinationTrack)
    selectStationForTrack(autoDestinationTrack);
  autoAppliedTrainGuid = autoDetectedTrainGuid;
}

function refreshCurrentTrain() {
  return fetchJson(new URL('/currentTrain', location))
    .then(current => {
      const status = document.getElementById('routeCurrentTrain');
      if (!current.inTrain || current.trainsetId < 0) {
        autoDetectedTrainGuid = null;
        autoAppliedTrainGuid = null;
        autoDestinationTrack = null;
        if (status)
          status.textContent = 'Not aboard a train.';
        return;
      }

      if (autoDetectedTrainGuid !== current.carGuid) {
        // A different train: allow the selection to move again.
        autoAppliedTrainGuid = null;
      }
      autoDetectedTrainGuid = current.carGuid;
      autoDestinationTrack = current.destinationTrack || null;
      applyAutoSelection();

      if (status) {
        const parts = [`Aboard ${current.carId}`];
        if (current.jobId)
          parts.push(`job ${current.jobId}`);
        parts.push(current.destinationTrack
          ? `to ${current.destinationTrack}`
          : (current.jobId ? 'no destination track on job' : 'no job'));
        status.textContent = parts.join(' – ');
      }
    })
    .catch(() => {});
}

document.getElementById('routeSetButton')
  .addEventListener('click', () => {
    const trainsetId = routeTrainSelect.value;
    const trackId = routeTrackSelect.value;
    if (!trainsetId || !trackId) {
      routeMessage.textContent = 'Select a train and a destination track.';
      return;
    }
    routeMessage.textContent = 'Planning...';
    fetchJson(new URL(`/route/${trainsetId}/${encodeURIComponent(trackId)}`, location), { method: 'POST', body: '' })
      .then(route => {
        routeMessage.textContent = route.message || route.status;
        refreshRoutes();
      })
      .catch(error => { routeMessage.textContent = error.message || 'Routing failed.'; });
  });

/////////////////////
// junctions

let junctions = [];
const junctionsReady = tracksReady
.then(_ => fetchJson(new URL('/junction', location)))
.then(allJunctionData =>
  junctions = allJunctionData.map((data, index) => ({
    marker: createJunctionMarker(data.position, index),
    branches: data.branches,
  }))
);

function toggleJunction(junctionId) {
  fetchJson(new URL(`/junction/${junctionId}/toggle`, location), { method: 'POST' })
  .then(selectedBranch => updateJunctionOverlay(junctionId, selectedBranch))
  .catch(err => {});
}

const junctionCanvasSize = 30;

function createJunctionShape(selectedBranch) {
  return `<g opacity="70%"><rect x="${-junctionCanvasSize/2}" y="${-junctionCanvasSize}" width="${junctionCanvasSize}" height="${junctionCanvasSize*2}" fill="red"/>` +
    (
      selectedBranch == 0 ? `<line x1="${junctionCanvasSize/2}" y1="${junctionCanvasSize}" x2="${-junctionCanvasSize/2}" y2="${-junctionCanvasSize}" stroke="white" stroke-width="10"/>` :
      selectedBranch == 1 ? `<line x1="${-junctionCanvasSize/2}" y1="${junctionCanvasSize}" x2="${junctionCanvasSize/2}" y2="${-junctionCanvasSize}" stroke="white" stroke-width="10"/>`
      : ''
    ) +
    `<rect x="${-junctionCanvasSize/2}" y="${-junctionCanvasSize}" width="${junctionCanvasSize}" height="${junctionCanvasSize*2}" fill="none" stroke="black" stroke-width="2%"/></g>`;
}

function createJunctionLabel(junctionId) {
  return `<text x="${-junctionCanvasSize/2+5}" y="${junctionCanvasSize-5}">${junctionId}</text>`
}

function createJunctionOverlay(junctionId) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('id', `J-${junctionId}`)
  svg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  svg.setAttribute('viewBox', `${-junctionCanvasSize/2} ${-junctionCanvasSize} ${junctionCanvasSize} ${junctionCanvasSize*2}`);
  svg.innerHTML = createJunctionShape(null) + createJunctionLabel(junctionId);
  return svg;
}

function updateJunctionOverlay(junctionId, selectedBranch) {
  const junction = junctions[junctionId]
  junction.marker.getElement().innerHTML = createJunctionShape(selectedBranch) + createJunctionLabel(junctionId);
  const selectedTrackId = junction.branches[selectedBranch]
  trackPolyLines.get(selectedTrackId).setStyle({ color: 'steelblue', dashArray: null });
  const unselectedTrackPolyLine = trackPolyLines.get(junction.branches[1-selectedBranch]);
  unselectedTrackPolyLine
    .setStyle({ color: 'lightsteelblue', dashArray: "6 12" })
    .bringToBack();
}

function getJunctionOverlayBounds(position) {
  const size = metersToDegrees * 5;
  return [ [ position[0] - size, position[1] - size/2], [position[0] + size, position[1] + size/2] ];
}

function createJunctionMarker(p, junctionId) {
  return L.svgOverlay(
    createJunctionOverlay(junctionId),
    getJunctionOverlayBounds(p),
    { interactive: true, renderer: canvasRenderer })
    .addEventListener('click', () => toggleJunction(junctionId) )
    .addTo(map)
    .setZIndex(Math.floor(p[0] * 100000 + p[1] * 100000));
}

function updateAllJunctions(states) {
  states.forEach((state, index) => updateJunctionOverlay(index, state))
}

/////////////////////
// following

function followCar(carId, shouldScroll) {
  setMarkerToFollow(carMarkers.get(carId));

  for (const row of carListBody.querySelectorAll('.following'))
    row.classList.remove('following');
  const carListRow = document.getElementById(`carList-${carId}`)
  carListRow.classList.add('following');
  if (shouldScroll)
    carListRow.scrollIntoView({ block: 'center' });

  for (const elem of jobListBody.querySelectorAll('.following'))
    elem.classList.remove('following');
  const jobListElems = jobListBody.querySelectorAll(`.jobList-carCell-${carId}`);
  for (const elem of jobListElems) {
    elem.classList.add('following');
    elem.closest('tbody').classList.add('following');
  }
  if (shouldScroll && jobListElems.length > 0)
    jobListElems[0].scrollIntoView({ block: 'center' });
}

/////////////////////
// player

const playerMarkers = new Map();

function getPlayerOverlayBounds(position) {
  const size = metersToDegrees * 2;
  return [ [ position[0] - size, position[1] - size], [position[0] + size, position[1] + size] ];
}

function updatePlayerOverlays(data) {
  const existingPlayerIds = Array.from(playerMarkers.keys());
  // Remove markers from disconnected players
  existingPlayerIds
  .filter(id => !data.hasOwnProperty(id))
  .forEach(id => {
    removePlayerOverlay(id);
  });
  // Add markers for new players
  Object.entries(data)
  .filter(([id]) => !existingPlayerIds.includes(id))
  .forEach(([id, playerData]) => {
    createPlayerMarker(id, playerData);
  });
  Object.entries(data).forEach(([id, playerData]) => {
    const polygonElem = document.getElementById(`playerPolygon-${id}`);
    polygonElem.setAttribute('transform', `rotate(${playerData.rotation})`);
    playerMarkers.get(id).setBounds(getPlayerOverlayBounds(playerData.position));
  });
}

function removePlayerOverlay(id) {
  document.getElementById(`playerPolygon-${id}`)?.remove();
  playerMarkers.delete(id);
}

function createPlayerOverlay(id, playerData) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '-15 -15 30 30');
  const polygon = document.createElementNS(svg.namespaceURI, 'polygon');
  polygon.setAttribute('id', `playerPolygon-${id}`);
  polygon.setAttribute('fill', playerData.color);
  polygon.setAttribute('fill-opacity', '70%');
  polygon.setAttribute('stroke', 'black');
  polygon.setAttribute('stroke-width', '1%');
  polygon.setAttribute('points', '0,-10 10,10 0,5 -10,10');
  svg.appendChild(polygon);
  return svg;
}

function createPlayerMarker(id, playerData) {
  playerMarkers.set(id, L.svgOverlay(
    createPlayerOverlay(id, playerData),
    getPlayerOverlayBounds(playerData.position),
    { interactive: true, bubblingMouseEvents: false })
    .addEventListener('click', e => setMarkerToFollow(e.target))
    .addTo(map));
}

function scrollToTrack(trackId) {
  stopFollowing();
  const polyLine = trackPolyLines.get(trackId);
  if (polyLine)
    map.panTo(polyLine.getCenter());
}

fetchJson(new URL('/player', location))
.then(data => {
  updatePlayerOverlays(data);
  zoomToAllPlayers();
})
.catch(() => {});

/////////////////////
// loco control

const locoIdSelect = document.getElementById('locoControlLocoId');
function updateLocoList() {
  for (const elem of Array.from(locoIdSelect.children))
    elem.remove();
  const locoIds = Array.from(allCarData.entries())
    .filter(([_, carData]) => carData.canBeControlled)
    .map(([id, _]) => id.slice(2));
  locoIds.sort();
  for (const id of locoIds) {
    const option = document.createElement('option');
    option.textContent = id;
    locoIdSelect.appendChild(option);
  }
}

function isReverserButtonActive(faButton) {
  return faButton.querySelector('svg').getAttribute('data-prefix') == 'fas';
}

function updateReverserButtons(reverser) {
  const reverseButton = document.querySelector('#locoControlReverserReverseButton svg');
  const newReverseStyle = reverser < 0.5 ? 'fas' : 'far';
  if (reverseButton.getAttribute('data-prefix') != newReverseStyle)
    reverseButton.setAttribute('data-prefix', newReverseStyle);

  const forwardButton = document.querySelector('#locoControlReverserForwardButton svg');
  const newForwardStyle = reverser > 0.5 ? 'fas' : 'far';
  if (forwardButton.getAttribute('data-prefix') != newForwardStyle)
    forwardButton.setAttribute('data-prefix', newForwardStyle);
}

const locoBrakePipeDisplay = document.getElementById('locoControlBrakePipe');
const locoSpeedDisplay = document.getElementById('locoControlForwardSpeed');
const locoTrainBrakeInput = document.getElementById('locoControlTrainBrakeInput');
const locoIndependentBrakeInput = document.getElementById('locoControlIndependentBrakeInput');
const locoReverserReverseButton = document.getElementById('locoControlReverserReverseButton');
const locoReverserForwardButton = document.getElementById('locoControlReverserForwardButton');
const locoThrottleInput = document.getElementById('locoControlThrottleInput');
const locoControlCoupleButton = document.getElementById('locoControlCoupleButton');
const locoControlUncoupleButton = document.getElementById('locoControlUncoupleButton');
const locoControlUncoupleSelect = document.getElementById('locoControlUncoupleSelect');

function updateCouplingControls(carData) {
  const canCouple = carData.canCouple;
  const carsInFront = carData.carsInFront;
  const carsInRear = carData.carsInRear;

  locoControlCoupleButton.disabled = !canCouple;
  locoControlUncoupleButton.disabled = carsInFront == 0 && carsInRear && 0;

  if (locoControlUncoupleSelect.childElementCount == carsInFront + carsInRear) {
    return;
  }

  const options = [];
  for (let i = carsInFront; i >= 1; i--)
    options.push(i);
  for (let i = 1; i <= carsInRear; i++)
    options.push(-i);
  locoControlUncoupleSelect.replaceChildren(...options.map(i => {
    const option = document.createElement('option');
    option.setAttribute('value', i);
    option.textContent = i >= 0 ? `\u002b${i}` : `\u2212${-i}`;
    return option;
  }));
}

function getControlledLocoGuid() {
  return allCarData.get(`L-${locoIdSelect.value}`)?.guid;
}

function getControlledLocoData() {
  const guid = getControlledLocoGuid();
  if (guid) {
    return fetchJson(new URL(`/car/${guid}`, location));
  }
}

let locoTrainBrakeEditing = false;
let locoIndependentBrakeEditing = false;
let locoThrottleEditing = false;

function updateLocoTrainBrakeInput(carData) {
  if (locoTrainBrakeEditing)
    return;
  locoTrainBrakeInput.value = carData.trainBrake * 100;
}

function updateLocoIndependentBrakeInput(carData) {
  if (locoIndependentBrakeEditing)
    return;
  locoIndependentBrakeInput.value = carData.independentBrake * 100;
}

function updateLocoThrottleInput(carData) {
  if (locoThrottleEditing)
    return;
  locoThrottleInput.value = carData.throttle * 100;
}

function updateLocoDisplay() {
  const pending = getControlledLocoData();
  if (!pending)
    return;
  pending
  .then(carData => {
    locoBrakePipeDisplay.textContent = carData.brakePipe.toFixed(1);
    locoSpeedDisplay.textContent = carData.forwardSpeed.toFixed(0);
    updateLocoTrainBrakeInput(carData);
    updateLocoIndependentBrakeInput(carData);
    updateReverserButtons(carData.reverser);
    updateLocoThrottleInput(carData);
    updateCouplingControls(carData);
  })
  .catch(() => {});
}

let locoControlRefreshIntervalId;
locoIdSelect.addEventListener('change', updateLocoDisplay);
sidebar.on("content", e => {
  clearInterval(locoControlRefreshIntervalId);
  if (e.id == "locoControlTab") {
    locoControlRefreshIntervalId = setInterval(updateLocoDisplay, 1000 / 9);
  }
});
sidebar.on("closing", e => {
  clearInterval(locoControlRefreshIntervalId);
  locoControlRefreshIntervalId = undefined;
})

function sendLocoCommand(command) {
  const guid = getControlledLocoGuid();
  if (guid) {
    fetch(new URL(`/car/${guid}/control?${command}`, location), { method: 'POST' });
  }
}

function rangeCommandSender(parameter) {
  return e => sendLocoCommand(`${parameter}=${e.target.value / 100}`);
}

locoTrainBrakeInput.addEventListener('input', rangeCommandSender('trainBrake'));
locoIndependentBrakeInput.addEventListener('input', rangeCommandSender('independentBrake'));
locoReverserReverseButton.addEventListener('click', e =>
  sendLocoCommand(`reverser=${isReverserButtonActive(locoReverserReverseButton) ? 0.5 : 0}`));
locoReverserForwardButton.addEventListener('click', e =>
  sendLocoCommand(`reverser=${isReverserButtonActive(locoReverserForwardButton) ? 0.5 : 1}`));
locoThrottleInput.addEventListener('input', rangeCommandSender('throttle'));
locoControlCoupleButton.addEventListener('click', e =>
  sendLocoCommand('couple=0'));
locoControlUncoupleButton.addEventListener('click', e =>
  sendLocoCommand(`uncouple=${locoControlUncoupleSelect.value}`));

locoTrainBrakeInput.addEventListener("mousedown", () => locoTrainBrakeEditing = true);
locoTrainBrakeInput.addEventListener("mouseup", () => {
  locoTrainBrakeEditing = false;
  updateLocoDisplay();
});
locoIndependentBrakeInput.addEventListener("mousedown", () => locoIndependentBrakeEditing = true);
locoIndependentBrakeInput.addEventListener("mouseup", () => {
  locoIndependentBrakeEditing = false;
  updateLocoDisplay();
});
locoThrottleInput.addEventListener("mousedown", () => locoThrottleEditing = true);
locoThrottleInput.addEventListener("mouseup", () => {
  locoThrottleEditing = false;
  updateLocoDisplay();
});


/////////////////////
// cars

const carWidthMeters = 3;
const carWidthPx = 20;
const svgPixelsPerMeter = carWidthPx / 3;

const allCarData = new Map();
const carMarkers = new Map();

function getCarColor(carId) {
  const jobId = carJobIds.get(carId);

  switch (getCarColorMode()) {
  case 'jobId':
    return jobId ? colorByHashing(jobId) : 'gray';
  case 'jobType':
    return jobId ? colorForJobType(jobId) : 'gray';
  case 'destination':
    return jobId ? colorForJobDestination(jobId) : 'gray';
  case 'carType':
    return colorByHashing(carId.slice(0,3));
  }
}

function updateCarColor(carId) {
  const carMarker = carMarkers.get(carId);
  const rect = carMarker.getElement().querySelector('rect');
  if (rect)
    rect.setAttribute('fill', getCarColor(carId));
}

function updateAllCarColors() {
  carMarkers.forEach((_, carId) => updateCarColor(carId));
}

const locoShapeNoseDepth = 10;

function getCarRenderWidthPx() {
  return carWidthPx * getCarWidthScale() / getCarLengthScale();
}

function createCarShape(carId, carData) {
  const isLoco = carId.slice(0,2) == 'L-';
  const lengthPx = carData.length * svgPixelsPerMeter;
  const widthPx = getCarRenderWidthPx();
  const svg = isLoco
    ? `<polygon points="${-lengthPx/2},-${widthPx/2} ${-lengthPx/2},${widthPx/2} ${lengthPx/2-locoShapeNoseDepth},${widthPx/2} ${lengthPx/2},0 ${lengthPx/2-locoShapeNoseDepth},-${widthPx/2}" fill="goldenrod" fill-opacity="70%" stroke="black" stroke-width="1%"/>`
    : `<rect x="${-lengthPx/2}" y="${-widthPx/2}" width="${lengthPx}" height="${widthPx}" fill-opacity="70%" stroke="black" stroke-width="1%"/>`;
  return svg;
}

function createCarLabel(carId, carData) {
  const isLoco = carId.slice(0,2) == 'L-';
  const jobId = carJobIds.get(carId);
  const lengthPx = carData.length * svgPixelsPerMeter;
  const rotation = carData.rotation >= 180 ? 'rotate(180)' : '';
  if (isLoco)
    return `<text transform="translate(-3 0) ${rotation}" text-anchor="middle" dominant-baseline="central" font-size="12" font-weight="bold">${carId}</text>`;
  const jobIdLabel =
    !jobId ? ""
    : jobId.split('-').length == 3 ? jobId.slice(-5,-3) + jobId.slice(-2)
    : jobId.split('-').join('');
  const jobIdText = `<text x="${-lengthPx/2 + 5}" transform="${rotation}" dominant-baseline="central" font-size="16">${jobIdLabel}</text>`
  const carIdText =
    `<text y="-0.5em" y="1" transform="${rotation} translate(${lengthPx/2 - 5})" dominant-baseline="central" text-anchor="end" font-size="8" font-family="monospace" font-weight="bold">` +
      `<tspan x="0">${carId.slice(0,-3).replaceAll('-', '')}</tspan>` +
      `<tspan x="0" dy="1em">${carId.slice(-3)}</tspan>` +
    '</text>';
  return jobIdText + carIdText;
}

function createCarOverlay(carId, carData) {
  const lengthPx = carData.length * svgPixelsPerMeter;
  const widthPx = getCarRenderWidthPx();
  const carCanvasMajor = Math.sqrt(lengthPx / 2 * lengthPx / 2 + widthPx / 2 * widthPx / 2);
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('id', carId);
  svg.setAttribute('xmlns', 'http://www.w3.org/2000/svg');
  svg.setAttribute('viewBox', `${-carCanvasMajor} ${-widthPx/2} ${carCanvasMajor*2} ${widthPx}`);
  return svg
}

function updateCarMarker(carId) {
  const marker = carMarkers.get(carId);
  if (!marker)
    return;
  const carData = allCarData.get(carId);
  marker.setBounds(getCarOverlayBounds(carData));
  marker.setRotationAngle(carData.rotation - 90);
  const svg = marker.getElement();
  const lengthPx = carData.length * svgPixelsPerMeter;
  const widthPx = getCarRenderWidthPx();
  const carCanvasMajor = Math.sqrt(lengthPx / 2 * lengthPx / 2 + widthPx / 2 * widthPx / 2);
  svg.setAttribute('viewBox', `${-carCanvasMajor} ${-widthPx/2} ${carCanvasMajor*2} ${widthPx}`);
  svg.innerHTML = createCarShape(carId, carData) + createCarLabel(carId, carData);
  updateCarColor(carId);
}

function getCarOverlayBounds(carData) {
  const position = carData.position;
  const length = metersToDegrees * carData.length * getCarLengthScale();
  const width = metersToDegrees * carWidthMeters * getCarWidthScale();
  return [ [ position[0] - width/2, position[1] - length/2], [position[0] + width/2, position[1] + length/2] ];
}

function createNewCar(carId, carData) {
  allCarData.set(carId, carData);
  createCarRow(carId);
  const overlay = L.svgOverlay(
    createCarOverlay(carId, carData),
    getCarOverlayBounds(carData),
    { interactive: true, bubblingMouseEvents: false })
    .addEventListener('mouseup', e => followCar(carId, true))
    .addTo(map);
  carMarkers.set(carId, overlay);
  updateCarMarker(carId);
}

function updateCar(carId, carData) {
  allCarData.set(carId, carData);
  updateCarRow(carId);
  updateCarMarker(carId);
}

function removeCar(carId) {
  removeCarRow(carId);
  const marker = carMarkers.get(carId);
  if (marker) {
    marker.remove();
    carMarkers.delete(carId);
  }
  allCarData.delete(carId);
}

function updateAllCars(updateCarData) {
  Object.entries(updateCarData).forEach(([carId, carData]) => {
    if (!carMarkers.has(carId))
      createNewCar(carId, carData);
    else
      updateCar(carId, carData);
  });
  for ([carId, _] of carMarkers)
    if (!updateCarData[carId])
      removeCar(carId);
  updateLocoList();
  updateRouteTrainList();
}

function updateCars(cars) {
  Object.entries(cars).forEach(([carId, carData]) =>
    updateCar(carId, carData));
}

/////////////////////
// events

function uuidv4() {
  return ([1e7]+-1e3+-4e3+-8e3+-1e11).replace(/[018]/g, c =>
    (c ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> c / 4).toString(16)
  );
}
const sessionId = uuidv4();
const updateInterval = 100;
let updateStart;

function updateOnce() {
  updateStart = performance.now();
  return fetchJson(new URL(`/updates/${sessionId}`, location))
  .then(updateData => {
    Object.entries(updateData).forEach(([tag, data]) => {
      switch (tag) {
      case 'cars':
        updateAllCars(data);
        break;
      case 'jobs':
        updateAllJobs(data);
        break;
      case 'junctions':
        updateAllJunctions(data);
        break;
      case 'player':
        updatePlayerOverlays(data);
        break;
      case 'routes':
        renderRoutes(data);
        break;
      default:
        const segments = tag.split('-');
        switch (segments[0]) {
        case 'trainset': updateCars(data); break;
        case 'carguid': updateCar(data.id, data); break;
        }
      }
    });
  })
  .then(_ => {
    if (markerToFollow)
      map.panTo(markerToFollow.getBounds().getCenter());
  });
}

function updateLoop() {
  // Reschedule whatever happened. A rejected poll - the game shutting down, a
  // dropped connection, a reply that could not be parsed - used to leave this
  // chain unresolved, which silently ended live updates for the whole session.
  updateOnce()
  .catch(() => {})
  .then(_ => {
    const timeToNextUpdate = (updateStart + updateInterval) - performance.now();
    setTimeout(updateLoop, Math.max(0, timeToNextUpdate));
  });
}

junctionsReady.then(_ => {
  updateLoop();
});

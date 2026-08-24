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

// Non-breaking hyphens, so a track ID is never split across two lines.
function replaceHyphens(s) { return s.replaceAll('-', '\u2011'); }

function jobElem(jobId, jobData) {
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

/////////////////////
// map picture

// A picture drawn under the railway - a community map or a render. Derail
// Valley is not a real place, so there is no aerial photography to fetch, and
// the terrain the game does have cannot be read from the mod: its distant
// meshes are not readable and its detailed terrain exists only where the player
// has been. The picture is lined up by its corners, given in the same world
// metres the tracks are drawn in, so it stays put however far anyone travels.
let mapImageOverlay = null;

// Its own pane, below the one Leaflet draws overlays into, so the picture can
// never end up over the railway whatever order things load in.
map.createPane('mapPicture');
map.getPane('mapPicture').style.zIndex = 350;
map.getPane('mapPicture').style.pointerEvents = 'none';

function refreshMapPicture() {
  return fetchJson(new URL('/mapOverlay', location))
    .then(info => {
      if (mapImageOverlay) {
        map.removeLayer(mapImageOverlay);
        mapImageOverlay = null;
      }
      reportMapPicture(info);
      if (!info || !info.enabled || !Array.isArray(info.bounds))
        return;
      // Cache-busted, so replacing the file on disk shows up on a reload
      // rather than being served from the browser's copy for ever.
      const url = new URL('/mapOverlay/image', location);
      url.searchParams.set('t', String(Date.now()));
      mapImageOverlay = L.imageOverlay(url.href, info.bounds, {
        opacity: typeof info.opacity === 'number' ? info.opacity : 0.75,
        pane: 'mapPicture',
        interactive: false,
      }).addTo(map);
    })
    .catch(() => {});
}

// Where the rails actually are, so the corners can be lined up against
// something rather than guessed at.
function reportMapPicture(info) {
  const status = document.getElementById('mapPictureStatus');
  if (!status)
    return;
  const rails = info && info.railBounds;
  const extent = rails
    ? `Rails span X ${Math.round(rails.minX)} to ${Math.round(rails.maxX)},`
      + ` Z ${Math.round(rails.minZ)} to ${Math.round(rails.maxZ)}.`
    : '';
  if (!info || !info.configured)
    status.textContent = 'No picture set. Choose one in the mod settings. ' + extent;
  else if (info.error)
    status.textContent = info.error + ' ' + extent;
  else if (!info.enabled)
    status.textContent = 'Picture turned off in the mod settings. ' + extent;
  else
    status.textContent = 'Picture shown. ' + extent;
}

/////////////////////
// tracks

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
      for (const track of station.tracks || []) {
        if (typeof track === 'string')
          trackLabels.set(track, track);
        else if (track.id)
          trackLabels.set(track.id, track.display || track.id);
      }
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

// Canonical track ID -> the display ID the selects show ("GF-D5I"), which is
// also what the game prints on jobs and lineside. A road loaded back into the
// planner then names its calls exactly as the dispatcher picked them, rather
// than showing raw IDs for the very same places.
const trackLabels = new Map();
const routeTrainSelect = document.getElementById('routeTrainSelect');
const routeStationSelect = document.getElementById('routeStationSelect');
const routeTrackSelect = document.getElementById('routeTrackSelect');
const routeMessage = document.getElementById('routeMessage');
const routeList = document.getElementById('routeList');
const routeStopList = document.getElementById('routeStopList');
const routeSetButton = document.getElementById('routeSetButton');

// The itinerary being built, before it is sent. Each entry is { id, label }:
// the id is what the host routes to - a track ID or a junction like "J-482" -
// and the label is what the player picked it by. Empty means the road is the
// single destination currently selected above, so the one-click case that was
// here before still works untouched.
const maxStops = 12;   // matches RouteDestination.MaxStops on the host
let plannedStops = [];

// The road being amended, if any.
//
// While this is held, the primary button updates that road instead of booking
// another, and says so. Editing used to live entirely in one line of message
// text, which the next click wrote over: from then on the panel looked exactly
// like building a new road, and the only way to find out which it would do was
// to press it.
let editing = null;            // { id, order, trainsetId, guid, color }
let stopsBeforeEdit = [];      // what was on the workbench, to put back on cancel
let trainBeforeEdit = null;

const routeEditingBar = document.getElementById('routeEditing');
const routeEditingLabel = document.getElementById('routeEditingLabel');

function renderEditing() {
  if (routeEditingBar) {
    routeEditingBar.hidden = editing === null;
    if (editing) {
      routeEditingBar.style.borderLeftColor = editing.color;
      if (routeEditingLabel) {
        routeEditingLabel.textContent =
          `Amending road ${editing.order}, train ${editing.trainsetId}`;
      }
    }
  }
  if (routeSetButton)
    routeSetButton.textContent = editing ? `Update road ${editing.order}` : 'Set route';
}

/// Leave editing without putting anything back: the amendment went through.
function stopEditing() {
  editing = null;
  stopsBeforeEdit = [];
  trainBeforeEdit = null;
}

/// Leave editing and undo it, so cancel really is a cancel - the workbench
/// goes back to whatever was on it before Edit was pressed, train included.
function cancelEdit(message) {
  if (!editing)
    return;
  const restored = stopsBeforeEdit.slice();
  const train = trainBeforeEdit;
  stopEditing();
  plannedStops = restored;
  if (train !== null
      && Array.from(routeTrainSelect.options).some(option => option.value === train))
    routeTrainSelect.value = train;
  renderPlannedStops();
  routeMessage.textContent = message || 'Left that road as it was.';
}

function stopLabelFor(id) {
  if (!id)
    return '';
  const planned = plannedStops.find(stop => stop.id === id);
  if (planned)
    return planned.label;
  if (/^J-\d+$/.test(id))
    return `Junction ${id.slice(2)}`;
  return trackLabels.get(id) || replaceHyphens(id);
}

function selectedTrackStop() {
  const id = routeTrackSelect.value;
  if (!id)
    return null;
  const option = routeTrackSelect.selectedOptions[0];
  return { id, label: option ? option.textContent : replaceHyphens(id) };
}

/// Draw the itinerary, and keep it an honest picture of what Set route will do.
///
/// With nothing booked, Set route runs to whatever track is selected below.
/// That used to be an invisible second mode - two buttons that each did a
/// different thing depending on state. The selected track is shown here as a
/// faint first call instead, so the list always says exactly where the train
/// will be sent.
function renderPlannedStops() {
  // A browser holding a cached index.html from before stops existed has no
  // list to draw into. Routing still works without it, so this gives way
  // rather than throwing and taking the rest of the page's script with it.
  if (!routeStopList)
    return;
  routeStopList.innerHTML = '';

  const clearButton = document.getElementById('routeClearStopsButton');
  if (clearButton)
    clearButton.hidden = plannedStops.length === 0;

  if (plannedStops.length === 0) {
    const preview = selectedTrackStop();
    const item = document.createElement('li');
    item.className = 'routeStopGhost';
    item.textContent = preview
      ? preview.label
      : 'Nothing booked yet.';
    routeStopList.appendChild(item);
    if (routeSetButton)
      routeSetButton.disabled = !preview;
    renderEditing();
    return;
  }

  if (routeSetButton)
    routeSetButton.disabled = false;

  // Read once for the whole list: every row offers to become this.
  const selected = selectedTrackStop();

  for (const [index, stop] of plannedStops.entries()) {
    const item = document.createElement('li');

    const label = document.createElement('span');
    label.className = 'routeStopLabel';
    label.textContent = stop.label;
    item.appendChild(label);

    const tools = document.createElement('span');
    tools.className = 'routeStopTools';

    // Swapping one call for another is the commonest amendment there is -
    // same road, different platform - and there was no way to do it. It meant
    // removing the call, adding the new one, which lands at the end, and then
    // nudging it back up past every call that came after it.
    tools.appendChild(stopButton('\u21c4', selected
      ? `Call here at ${selected.label} instead`
      : 'Choose a track below to put here instead',
      !selected || selected.id === stop.id, () => {
        plannedStops[index] = { id: selected.id, label: selected.label };
        renderPlannedStops();
        routeMessage.textContent = `Call ${index + 1} is now ${selected.label}.`;
      }));

    // Buttons rather than dragging: the panel is narrow, this has to work on a
    // phone propped on the desk, and an order of calls is short enough that
    // nudging one along is no hardship.
    tools.appendChild(stopButton('↑', 'Move earlier', index === 0, () => {
      [plannedStops[index - 1], plannedStops[index]] =
        [plannedStops[index], plannedStops[index - 1]];
      renderPlannedStops();
    }));
    tools.appendChild(stopButton('↓', 'Move later',
      index === plannedStops.length - 1, () => {
        [plannedStops[index + 1], plannedStops[index]] =
          [plannedStops[index], plannedStops[index + 1]];
        renderPlannedStops();
      }));
    tools.appendChild(stopButton('×', 'Remove this call', false, () => {
      plannedStops.splice(index, 1);
      renderPlannedStops();
    }));
    item.appendChild(tools);
    routeStopList.appendChild(item);
  }
  renderEditing();
}

function stopButton(glyph, title, disabled, onClick) {
  const button = document.createElement('button');
  button.className = 'routeStopTool';
  button.textContent = glyph;
  button.title = title;
  button.disabled = disabled;
  button.addEventListener('click', onClick);
  return button;
}

// Calling twice at the same place in a row is a leg of no length, which would
// arrive the moment it was planned, so a repeat of the stop before is dropped.
function addPlannedStop(id, label) {
  if (!id)
    return;
  if (plannedStops.length >= maxStops) {
    routeMessage.textContent = `A road may call at up to ${maxStops} places.`;
    return;
  }
  const last = plannedStops[plannedStops.length - 1];
  if (last && last.id === id) {
    routeMessage.textContent = `${label} is already the last call.`;
    return;
  }
  plannedStops.push({ id, label });
  renderPlannedStops();
  routeMessage.textContent = plannedStops.length === 1
    ? 'One call booked. Add more, or press Set route.'
    : `${plannedStops.length} calls booked.`;
}

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
  renderPlannedStops();
}

routeStationSelect.addEventListener('input', () => {
  updateRouteTrackList();
  renderPlannedStops();
});

// Keeps the faint first call under "Calling at" honest as the selection moves.
routeTrackSelect.addEventListener('input', renderPlannedStops);

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
// What colour each highlighted track is currently drawn in, so a redraw only
// touches the ones that changed. Restyling a whole road every second - and
// bringing each of its tracks to the front, which reorders the canvas each
// time - was quadratic work for a picture that had usually not moved.
let highlightedTracks = new Map();

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

  const releasedJunctions = new Set();
  for (const trackId of highlightedTracks.keys()) {
    if (colorByTrack.has(trackId))
      continue;
    const polyline = trackPolyLines.get(trackId);
    if (polyline)
      polyline.setStyle({ color: baseTrackColor(trackId), weight: 3 });
    for (const junctionId of junctionsByBranchTrack.get(trackId) || [])
      releasedJunctions.add(junctionId);
  }

  for (const [trackId, color] of colorByTrack) {
    if (highlightedTracks.get(trackId) === color)
      continue;
    const polyline = trackPolyLines.get(trackId);
    if (!polyline)
      continue;
    polyline.setStyle({ color: color, weight: 5 });
    polyline.bringToFront();
  }

  highlightedTracks = colorByTrack;

  // Last, so a branch a road has just let go shows which way its switch lies
  // rather than the plain track colour.
  for (const junctionId of releasedJunctions)
    repaintJunction(junctionId);
}

/// One card per road, keyed to the colour it is drawn in on the map and
/// headed by whoever booked it.
///
/// This was a six-column table in a sidebar four inches wide, where the
/// operator was a cramped column and the colour a dot that took some finding.
/// The card carries the colour twice - as the stripe down its edge and as the
/// numbered badge - and gives the name that booked it top billing, which is
/// what a second dispatcher needs to see at a glance.
function renderRoutes(routes) {
  applyRouteHighlight(routes);
  routeList.innerHTML = '';

  const live = routes.filter(route => route.status !== 'Cleared');

  // A road that has been cleared, or has run its course, is not there to amend
  // any more. Left alone the bar went on offering to update it, and the button
  // would have quietly booked a new road under its name. The calls stay on the
  // workbench - they are still what was wanted, and can be booked afresh.
  if (editing && !live.some(entry => entry.id === editing.id)) {
    const order = editing.order;
    stopEditing();
    renderPlannedStops();
    routeMessage.textContent = `Road ${order} is no longer set:`
      + ' this will book a new road for the train chosen.';
  }

  if (live.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'routeEmpty';
    empty.textContent = 'No roads set.';
    routeList.appendChild(empty);
    return;
  }

  for (const route of live) {
    const color = routeColor(route);
    const card = document.createElement('div');
    card.className = 'routeCard';
    card.style.borderLeftColor = color;

    const head = document.createElement('div');
    head.className = 'routeCardHead';

    const badge = document.createElement('span');
    badge.className = 'routeBadge';
    badge.style.backgroundColor = color;
    badge.textContent = String(routeOrder(route));
    badge.title = 'Roads are numbered and coloured in the order they were set.';
    head.appendChild(badge);

    const who = document.createElement('span');
    who.className = 'routeOperator';
    // Falls back only when neither the page sign-in nor the Multiplayer mod
    // had a name to give - a single player who never set one.
    who.textContent = route.requestedBy || 'This machine';
    head.appendChild(who);

    const train = document.createElement('span');
    train.className = 'routeTrain';
    train.textContent = `train ${route.trainsetId}`;
    head.appendChild(train);

    // Amending used to mean clearing the road and building the whole
    // itinerary again from nothing. This loads the calls it has left back
    // into the planner, so changing a platform is: edit, swap the call,
    // Set route. Setting a road for a train replaces the one it has, so
    // there is no clearing step and no moment with no road booked.
    const edit = document.createElement('button');
    edit.className = 'routeEdit';
    edit.textContent = 'Edit';
    edit.title = 'Load this road’s remaining calls into the planner';
    edit.addEventListener('click', () => editRoute(route));
    head.appendChild(edit);

    const clear = document.createElement('button');
    clear.className = 'routeClear';
    clear.textContent = 'Clear';
    clear.title = 'Release this road and its junctions';
    clear.addEventListener('click', () => clearRoute(route.id));
    head.appendChild(clear);
    card.appendChild(head);

    // Where it is going now, and how far through the booking that is: only the
    // leg in hand is ever drawn on the map.
    const stops = Array.isArray(route.stops) ? route.stops : [];
    const dest = document.createElement('div');
    dest.className = 'routeCardDest';
    const arrow = document.createElement('span');
    arrow.className = 'routeArrow';
    arrow.textContent = '→ ';
    dest.appendChild(arrow);
    dest.appendChild(document.createTextNode(stopLabelFor(route.destinationTrack)));
    if (stops.length > 1) {
      const progress = document.createElement('span');
      progress.className = 'routeStopProgress';
      progress.textContent = ` call ${(route.stopIndex || 0) + 1} of ${stops.length}`;
      dest.appendChild(progress);
      // Built as text, not interpolated markup: track names come from the
      // world and from other mods, and a quote in one used to break out of the
      // title attribute.
      dest.title = 'Calling at ' + stops.map(stopLabelFor).join(', ');
    }
    card.appendChild(dest);

    const status = document.createElement('div');
    status.className = `routeStatus routeStatus-${route.status}`;
    status.textContent = route.status === 'AwaitingReversal'
      ? 'Awaiting reversal' : route.status;
    card.appendChild(status);

    // Some of these are instructions the driver has to act on - draw forward
    // past a signal and set back, or stand short of a crossing until it clears
    // - so they belong on the card, not hidden in a tooltip.
    if (route.message) {
      const note = document.createElement('div');
      note.className = 'routeMessage'
        + (route.status === 'AwaitingReversal' ? ' routeMessage-action' : '');
      note.textContent = route.message;
      card.appendChild(note);
    }

    routeList.appendChild(card);
  }
}

/// Put a booked road back on the workbench.
///
/// Only the calls it has still to make: the ones already behind it are not
/// somewhere it needs sending again, and a road amended mid-journey should
/// carry on from where the train actually is.
function editRoute(route) {
  // Remember the workbench as it stands, but only on the way in: pressing Edit
  // on a second road while already editing a first should not make cancel put
  // the first road's calls back as though they were the dispatcher's own.
  if (!editing) {
    stopsBeforeEdit = plannedStops.slice();
    trainBeforeEdit = routeTrainSelect.value;
  }

  const guid = guidForTrainset(route.trainsetId);
  if (guid)
    routeTrainSelect.value = guid;
  // Leave the auto-selection alone from here, or riding in a train would drag
  // the choice back the moment the next poll landed.
  autoAppliedTrainGuid = autoDetectedTrainGuid;

  const stops = Array.isArray(route.stops) ? route.stops : [];
  const remaining = stops.slice(route.stopIndex || 0);
  // Emptied first so the calls are named from the track list rather than from
  // whatever happened to be on the workbench a moment ago.
  plannedStops = [];
  plannedStops = remaining.map(id => ({ id, label: stopLabelFor(id) }));

  editing = {
    id: route.id,
    order: routeOrder(route),
    trainsetId: route.trainsetId,
    guid,
    color: routeColor(route),
  };
  renderPlannedStops();
  routeMessage.textContent = plannedStops.length > 0
    ? 'Change the calls below, then press Update.'
    : 'That road has no calls left. Add one, or cancel.';
  if (!guid)
    routeMessage.textContent += ' Its train is no longer listed - choose one.';
}

if (routeEditingBar) {
  document.getElementById('routeEditCancelButton')
    .addEventListener('click', () => cancelEdit());
}

// Amending road 3 and then picking a different train is not an amendment any
// more. The calls stay on the workbench - they are still what was wanted - but
// this books a new road now, and the button has to stop claiming otherwise.
routeTrainSelect.addEventListener('input', () => {
  if (!editing || (editing.guid && routeTrainSelect.value === editing.guid))
    return;
  const order = editing.order;
  stopEditing();
  renderPlannedStops();
  routeMessage.textContent = `No longer amending road ${order}:`
    + ' this will book a new road for the train now chosen.';
});

function guidForTrainset(trainsetId) {
  // Prefer a locomotive, since that is how the train list names a consist.
  let fallback = null;
  for (const [carId, carData] of allCarData) {
    if (carData.trainsetId !== trainsetId)
      continue;
    if (carId.startsWith('L-'))
      return carData.guid;
    if (!fallback)
      fallback = carData.guid;
  }
  return fallback;
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

// Every place the detected job asks for, in order. Offered rather than applied:
// booking it would throw away an itinerary the dispatcher had built by hand.
let autoJobStops = [];

function updateUseJobButton() {
  const button = document.getElementById('routeUseJobButton');
  if (!button)
    return;
  button.hidden = autoJobStops.length === 0;
  button.textContent = autoJobStops.length > 1
    ? `use job route (${autoJobStops.length} calls)`
    : 'use job route';
}

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
        // The preview under "Calling at" follows the selection, and this moved
        // it without going through the change event that normally redraws it.
        renderPlannedStops();
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
        autoJobStops = [];
        updateUseJobButton();
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
      autoJobStops = Array.isArray(current.stops) ? current.stops : [];
      applyAutoSelection();
      updateUseJobButton();

      if (status) {
        const parts = [`Aboard ${current.carId}`];
        if (current.jobId)
          parts.push(`job ${current.jobId}`);
        // A job worked by cars behind the loco is the normal case, so say how
        // many were found rather than leaving "no job" to look like a fault.
        const otherJobs = (current.jobs || []).length - (current.jobId ? 1 : 0);
        if (autoJobStops.length > 1)
          parts.push(`${autoJobStops.length} calls, ending ${current.destinationTrack}`);
        else if (current.destinationTrack)
          parts.push(`to ${current.destinationTrack}`);
        else
          parts.push(current.jobId ? 'no destination track on job' : 'no job on this train');
        if (otherJobs > 0)
          parts.push(`+${otherJobs} other job${otherJobs > 1 ? 's' : ''} aboard`);
        status.textContent = parts.join(' – ');
      }
    })
    .catch(() => {});
}

const routeUseJobButton = document.getElementById('routeUseJobButton');
if (routeUseJobButton) {
  routeUseJobButton.addEventListener('click', () => {
    const booked = autoJobStops.slice(0, maxStops);
    plannedStops = [];
    plannedStops = booked.map(id => ({ id, label: stopLabelFor(id) }));
    renderPlannedStops();
    routeMessage.textContent = booked.length < autoJobStops.length
      ? `Booked the first ${booked.length} of the job's ${autoJobStops.length} calls;`
        + ` a road may call at up to ${maxStops} places.`
      : `Booked the job's ${booked.length} call${booked.length > 1 ? 's' : ''}.`;
  });
}

const routeClearStopsButton = document.getElementById('routeClearStopsButton');
if (routeClearStopsButton) {
  routeClearStopsButton.addEventListener('click', () => {
    plannedStops = [];
    renderPlannedStops();
    routeMessage.textContent = '';
  });
}

document.getElementById('routeAddStopButton')
  .addEventListener('click', () => {
    const stop = selectedTrackStop();
    if (!stop) {
      routeMessage.textContent = 'Choose a station and a track first.';
      return;
    }
    addPlannedStop(stop.id, stop.label);
  });

routeSetButton
  .addEventListener('click', () => {
    const trainsetId = routeTrainSelect.value;
    // With nothing booked the selected track is the whole road - which the
    // list shows as a faint first call, so this is not a hidden second mode.
    const preview = selectedTrackStop();
    const stops = plannedStops.length > 0
      ? plannedStops.map(stop => stop.id)
      : (preview ? [preview.id] : []);
    if (!trainsetId) {
      routeMessage.textContent = 'Choose a train first.';
      return;
    }
    if (stops.length === 0) {
      routeMessage.textContent = 'Add a call, or choose a track to run to.';
      return;
    }
    routeMessage.textContent = 'Planning...';
    const itinerary = encodeURIComponent(stops.join('|'));
    fetchJson(new URL(`/route/${trainsetId}/${itinerary}`, location), { method: 'POST', body: '' })
      .then(route => {
        routeMessage.textContent = route.message || route.status;
        // A road that would not plan leaves the itinerary where it is. Clearing
        // it whatever came back threw away everything just built at the one
        // moment it was needed most - to change a call and try again. The host
        // keeps the old road on a failure to match, so an amendment that will
        // not plan costs the train nothing.
        if (route.status === 'Failed') {
          refreshRoutes();
          return;
        }
        stopEditing();
        plannedStops = [];
        renderPlannedStops();
        refreshRoutes();
      })
      .catch(error => { routeMessage.textContent = error.message || 'Routing failed.'; });
  });

renderPlannedStops();
updateUseJobButton();

/////////////////////
// junctions

let junctions = [];

// Which junctions draw each track. A junction branch's colour says which way
// the switch lies, and a road highlighted over it has to hand that back when
// it clears - which nothing did, so a cleared road used to leave the branch in
// the plain track colour until some switch elsewhere happened to repaint it.
const junctionsByBranchTrack = new Map();

const junctionsReady = tracksReady
.then(_ => fetchJson(new URL('/junction', location)))
.then(allJunctionData => {
  junctions = allJunctionData.map((data, index) => ({
    marker: createJunctionMarker(data.position, index),
    branches: data.branches,
  }));
  for (const [index, junction] of junctions.entries()) {
    for (const trackId of junction.branches || []) {
      if (!junctionsByBranchTrack.has(trackId))
        junctionsByBranchTrack.set(trackId, []);
      junctionsByBranchTrack.get(trackId).push(index);
    }
  }
});

// Draw a junction's branches as they currently lie, whatever was drawn over
// them since.
function repaintJunction(junctionId) {
  const junction = junctions[junctionId];
  if (!junction || junction.selectedBranch === undefined)
    return;
  const selected = junction.selectedBranch;
  junction.selectedBranch = undefined;
  updateJunctionOverlay(junctionId, selected);
}

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

// Throwing one switch used to redraw every junction on the map: the update
// carries the state of all of them, and each was re-rendered whether or not it
// had moved - reparsing its SVG and restyling two polylines apiece. Only the
// ones that actually changed are touched now.
function updateJunctionOverlay(junctionId, selectedBranch) {
  const junction = junctions[junctionId];
  if (!junction || junction.selectedBranch === selectedBranch)
    return;
  junction.selectedBranch = selectedBranch;

  junction.marker.getElement().innerHTML = createJunctionShape(selectedBranch) + createJunctionLabel(junctionId);

  // A booked road is drawn over its junctions, and outranks them: throwing a
  // switch under a highlighted road must not paint the road out.
  const selectedTrackId = junction.branches[selectedBranch];
  const selectedTrackPolyLine = trackPolyLines.get(selectedTrackId);
  if (selectedTrackPolyLine && !highlightedTracks.has(selectedTrackId))
    selectedTrackPolyLine.setStyle({ color: 'steelblue', dashArray: null });

  const unselectedTrackId = junction.branches[1-selectedBranch];
  const unselectedTrackPolyLine = trackPolyLines.get(unselectedTrackId);
  if (unselectedTrackPolyLine && !highlightedTracks.has(unselectedTrackId))
    unselectedTrackPolyLine
      .setStyle({ color: 'lightsteelblue', dashArray: "6 12" })
      .bringToBack();
}

function getJunctionOverlayBounds(position) {
  const size = metersToDegrees * 5;
  return [ [ position[0] - size, position[1] - size/2], [position[0] + size, position[1] + size/2] ];
}

// A junction is a place a train can be sent, not only a switch to throw: the
// throat of a yard has no track ID to pick from the list, and running up to it
// and shunting the rest by hand is often what is actually wanted. Left-click
// still throws the switch, so nothing that worked before has moved.
function openJunctionRouteMenu(event, p, junctionId) {
  // Leaflet hands layer handlers its own event object; the browser's menu is
  // suppressed on the DOM event inside it.
  L.DomEvent.preventDefault(event.originalEvent || event);
  const id = `J-${junctionId}`;
  const label = `Junction ${junctionId}`;
  const content = document.createElement('div');
  content.className = 'junctionMenu';

  const title = document.createElement('div');
  title.className = 'junctionMenuTitle';
  title.textContent = label;
  content.appendChild(title);

  const routeHere = document.createElement('button');
  routeHere.textContent = 'Route here';
  routeHere.addEventListener('click', () => {
    plannedStops = [{ id, label }];
    renderPlannedStops();
    map.closePopup();
    openRoutingTab();
    document.getElementById('routeSetButton').click();
  });
  content.appendChild(routeHere);

  const addStop = document.createElement('button');
  addStop.textContent = 'Add as stop';
  addStop.addEventListener('click', () => {
    addPlannedStop(id, label);
    map.closePopup();
    openRoutingTab();
  });
  content.appendChild(addStop);

  L.popup({ closeButton: true })
    .setLatLng([p[0], p[1]])
    .setContent(content)
    .openOn(map);
}

function openRoutingTab() {
  sidebar.open('routingTab');
}

function createJunctionMarker(p, junctionId) {
  return L.svgOverlay(
    createJunctionOverlay(junctionId),
    getJunctionOverlayBounds(p),
    { interactive: true, renderer: canvasRenderer })
    .addEventListener('click', () => toggleJunction(junctionId) )
    .addEventListener('contextmenu', e => openJunctionRouteMenu(e, p, junctionId) )
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

document.getElementById('mapPictureReload')
  ?.addEventListener('click', refreshMapPicture);

junctionsReady.then(_ => {
  refreshMapPicture();
  updateLoop();
});

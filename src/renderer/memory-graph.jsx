import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { Maximize2, ZoomIn, ZoomOut } from 'lucide-react';
import { Empty, IconButton } from './ui.jsx';

const width = 900;
const height = 430;
const point = edge => ({ from: edge.fromId || edge.from, to: edge.toId || edge.to });
const weightOf = edge => Math.max(0, Math.min(1, Number.isFinite(edge.weight) ? edge.weight : Number.isFinite(edge.semanticScore) ? edge.semanticScore : 1));
const semanticEdge = edge => edge.inferred || ['semantic', 'similar', 'similarity'].includes(edge.type);
const percent = value => `${Math.round(value * 100)}%`;
const edgeLabel = edge => semanticEdge(edge) ? 'Semantic similarity' : (edge.type || 'related').replace(/[_-]/g, ' ');
function edgeDescription(edge) {
  return [edgeLabel(edge), `strength ${percent(weightOf(edge))}`,
    Number.isFinite(edge.semanticScore) && `semantic ${percent(edge.semanticScore)}`,
    Number.isFinite(edge.correlationScore) && `correlation ${percent(edge.correlationScore)}`,
    Number.isFinite(edge.distance) && `distance ${edge.distance.toFixed(2)}`].filter(Boolean).join(' · ');
}

function layout(nodes, edges) {
  const positions = new Map(nodes.map((node, index) => {
    const angle = index * Math.PI * (3 - Math.sqrt(5));
    const radius = nodes.length === 1 ? 0 : 45 + Math.sqrt(index / Math.max(1, nodes.length - 1)) * 145;
    return [node.id, { x: width / 2 + Math.cos(angle) * radius * 1.8, y: height / 2 + Math.sin(angle) * radius }];
  }));
  for (let tick = 0; tick < 110; tick++) {
    const forces = new Map(nodes.map(node => [node.id, { x: 0, y: 0 }]));
    for (let index = 0; index < nodes.length; index++) {
      const left = positions.get(nodes[index].id);
      for (let other = index + 1; other < nodes.length; other++) {
        const right = positions.get(nodes[other].id);
        const dx = left.x - right.x;
        const dy = left.y - right.y;
        const distance = Math.max(1, Math.hypot(dx, dy));
        const strength = 4200 / (distance * distance);
        let forceX = dx / distance * strength;
        let forceY = dy / distance * strength;
        const overlapX = 188 - Math.abs(dx);
        const overlapY = 72 - Math.abs(dy);
        if (overlapX > 0 && overlapY > 0) {
          if (overlapX < overlapY) forceX += Math.sign(dx || 1) * overlapX * 0.12;
          else forceY += Math.sign(dy || 1) * overlapY * 0.12;
        }
        forces.get(nodes[index].id).x += forceX;
        forces.get(nodes[index].id).y += forceY;
        forces.get(nodes[other].id).x -= forceX;
        forces.get(nodes[other].id).y -= forceY;
      }
    }
    for (const edge of edges) {
      const { from, to } = point(edge);
      const left = positions.get(from);
      const right = positions.get(to);
      if (!left || !right) continue;
      const dx = right.x - left.x;
      const dy = right.y - left.y;
      const distance = Math.max(1, Math.hypot(dx, dy));
      const weight = weightOf(edge);
      const strength = (distance - (260 - weight * 115)) * (0.008 + weight * 0.025);
      forces.get(from).x += dx / distance * strength;
      forces.get(from).y += dy / distance * strength;
      forces.get(to).x -= dx / distance * strength;
      forces.get(to).y -= dy / distance * strength;
    }
    for (const node of nodes) {
      const position = positions.get(node.id);
      const force = forces.get(node.id);
      const cooling = 1 - tick / 140;
      position.x = Math.max(94, Math.min(width - 94, position.x + (force.x + (width / 2 - position.x) * 0.006) * cooling));
      position.y = Math.max(48, Math.min(height - 48, position.y + (force.y + (height / 2 - position.y) * 0.006) * cooling));
    }
  }
  return Object.fromEntries(positions);
}

const kindLabel = kind => kind === 'parameter' ? 'Parameter' : kind === 'entity' ? 'Entity' : 'Memory';
const shortLabel = value => value.length > 20 ? `${value.slice(0, 19)}…` : value;

function retainPositions(nodes, initial, previous) {
  const positions = Object.fromEntries(nodes.filter(node => previous[node.id]).map(node => [node.id, previous[node.id]]));
  for (const node of nodes) {
    if (positions[node.id]) continue;
    const preferred = initial[node.id];
    const candidates = [preferred];
    for (let x = 94; x <= width - 94; x += 24) for (let y = 48; y <= height - 48; y += 24) candidates.push({ x, y });
    let best;
    let bestOverlap = Infinity;
    let bestDistance = Infinity;
    for (const candidate of candidates) {
      const overlap = Object.values(positions).reduce((total, position) => total
        + Math.max(0, 180 - Math.abs(candidate.x - position.x)) * Math.max(0, 72 - Math.abs(candidate.y - position.y)), 0);
      const distance = Math.hypot(candidate.x - preferred.x, candidate.y - preferred.y);
      if (overlap < bestOverlap || (overlap === bestOverlap && distance < bestDistance)) {
        best = candidate; bestOverlap = overlap; bestDistance = distance;
      }
    }
    positions[node.id] = best;
  }
  return positions;
}

export function MemoryGraph({ graph, selectedId, onSelect }) {
  const nodes = graph?.nodes || [];
  const compact = nodes.length > 18;
  const [lens, setLens] = useState('all');
  const [minimum, setMinimum] = useState(0);
  const edges = (graph?.edges || []).filter(edge => weightOf(edge) >= minimum && (lens === 'all' || (lens === 'semantic' ? semanticEdge(edge) : lens === 'conflicts' ? edge.type === 'contradicts' : !semanticEdge(edge))));
  const signature = nodes.map(node => node.id).join('|') + edges.map(edge => `${edge.fromId || edge.from}/${edge.toId || edge.to}/${weightOf(edge)}`).join('|');
  const initial = useMemo(() => layout(nodes, edges), [signature]);
  const [positions, setPositions] = useState(initial);
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const drag = useRef(null);
  const marker = useId().replace(/:/g, '');
  useEffect(() => {
    setPositions(previous => retainPositions(nodes, initial, previous));
  }, [initial]);
  const fit = () => { setPositions(initial); setZoom(1); setPan({ x: 0, y: 0 }); };
  if (!nodes.length) return <Empty title="Your Brain starts here" detail="Agents build reusable knowledge as they work. Import existing provider memories when you choose." />;
  return <div className="memory-map">
    <div className="memory-map-tools"><div className="memory-map-legend"><span><i className="memory-kind-memory" />Memory</span><span><i className="memory-kind-entity" />Entity</span><span><i className="memory-kind-parameter" />Parameter</span></div><div className="actions"><IconButton icon={ZoomOut} label="Zoom out memory map" disabled={zoom <= 0.6} onClick={() => setZoom(value => Math.max(0.6, value / 1.2))} /><span className="memory-map-scale">{Math.round(zoom * 100)}%</span><IconButton icon={ZoomIn} label="Zoom in memory map" disabled={zoom >= 2.5} onClick={() => setZoom(value => Math.min(2.5, value * 1.2))} /><IconButton icon={Maximize2} label="Reset memory map" onClick={fit} /></div></div>
    <div className="memory-map-filters"><label>Explore<select aria-label="Graph relationships" value={lens} onChange={event => setLens(event.target.value)}><option value="all">All connections</option><option value="semantic">Semantic similarities</option><option value="evidence">Evidence & revisions</option><option value="conflicts">Contradictions</option></select></label><label>Minimum strength<select aria-label="Minimum connection strength" value={minimum} onChange={event => setMinimum(Number(event.target.value))}><option value={0}>All strengths</option><option value={0.5}>50% and above</option><option value={0.7}>70% and above</option><option value={0.9}>90% and above</option></select></label><span>{edges.length} visible {edges.length === 1 ? 'connection' : 'connections'}</span></div>
    <svg className="memory-map-canvas" viewBox={`0 0 ${width} ${height}`} role="group" aria-label="Memory map" onPointerDown={event => {
      if (event.button !== 0 || event.target.closest('[data-memory-node]')) return;
      drag.current = { type: 'pan', clientX: event.clientX, clientY: event.clientY, origin: pan };
      event.currentTarget.setPointerCapture(event.pointerId);
    }} onPointerMove={event => {
      const current = drag.current;
      if (!current) return;
      const bounds = event.currentTarget.getBoundingClientRect();
      const dx = (event.clientX - current.clientX) * width / bounds.width;
      const dy = (event.clientY - current.clientY) * height / bounds.height;
      if (current.type === 'pan') setPan({ x: current.origin.x + dx, y: current.origin.y + dy });
      else setPositions(value => ({ ...value, [current.id]: { x: current.origin.x + dx / zoom, y: current.origin.y + dy / zoom } }));
    }} onPointerUp={() => { drag.current = null; }} onPointerCancel={() => { drag.current = null; }}>
      <defs><pattern id={`${marker}-grid`} width="22" height="22" patternUnits="userSpaceOnUse"><circle cx="1" cy="1" r="0.7" fill="currentColor" /></pattern><marker id={`${marker}-arrow`} markerWidth="7" markerHeight="7" refX="6" refY="3.5" orient="auto"><path d="M0,0 L7,3.5 L0,7" fill="currentColor" /></marker></defs>
      <rect width={width} height={height} fill={`url(#${marker}-grid)`} className="memory-map-grid" />
      <g transform={`translate(${width / 2 + pan.x} ${height / 2 + pan.y}) scale(${zoom}) translate(${-width / 2} ${-height / 2})`}>
        {edges.map((edge, index) => {
          const { from, to } = point(edge);
          const start = positions[from];
          const end = positions[to];
          if (!start || !end) return null;
          const length = Math.max(1, Math.hypot(end.x - start.x, end.y - start.y));
          const offsetX = (end.x - start.x) / length * (compact ? 13 : 38);
          const offsetY = (end.y - start.y) / length * (compact ? 13 : 26);
          const selected = from === selectedId || to === selectedId;
          return <g key={edge.id || `${from}-${to}-${index}`} className={`memory-map-edge${selected ? ' selected' : ''}${semanticEdge(edge) ? ' inferred' : ''}${edge.type === 'contradicts' ? ' conflict' : ''}`}><title>{edgeDescription(edge)}</title><line style={{ strokeWidth: 0.8 + weightOf(edge) * (selected ? 3 : 2), opacity: selected ? 1 : 0.35 + weightOf(edge) * 0.55 }} x1={start.x + offsetX} y1={start.y + offsetY} x2={end.x - offsetX} y2={end.y - offsetY} markerEnd={semanticEdge(edge) ? undefined : `url(#${marker}-arrow)`} />{selected && <text x={(start.x + end.x) / 2} y={(start.y + end.y) / 2 - 9} textAnchor="middle">{edgeLabel(edge)} · {percent(weightOf(edge))}</text>}</g>;
        })}
        {nodes.map(node => {
          const position = positions[node.id];
          if (!position) return null;
          return <g key={node.id} data-memory-node={node.id} role="button" tabIndex={0} aria-label={`Open memory ${node.title || node.id}${node.supersededBy ? ' (previous version)' : ''}${node.archivedAt ? ' (archived)' : ''}`} aria-pressed={selectedId === node.id} className={`memory-map-node memory-kind-${node.kind || 'memory'}${compact ? ' network' : ''}${selectedId === node.id ? ' selected' : ''}${node.supersededBy || node.archivedAt ? ' historical' : ''}${node.lifecycle?.status === 'stale' ? ' stale' : ''}`} transform={`translate(${position.x} ${position.y})`} onClick={() => onSelect(node.id)} onKeyDown={event => { if (['Enter', ' '].includes(event.key)) { event.preventDefault(); onSelect(node.id); } }} onPointerDown={event => {
            if (event.button !== 0) return;
            event.stopPropagation();
            drag.current = { type: 'node', id: node.id, clientX: event.clientX, clientY: event.clientY, origin: position };
            event.currentTarget.setPointerCapture(event.pointerId);
          }}><title>{node.title || node.id}{node.lifecycle?.status === 'stale' ? ' · Needs review' : ''}</title>{compact ? <><circle r={selectedId === node.id ? 13 : 9} />{selectedId === node.id && <text x="0" y="31" textAnchor="middle" className="memory-map-node-title">{shortLabel(node.title || node.id)}</text>}</> : <><rect x="-85" y="-27" width="170" height="54" rx="10" /><circle cx="-68" cy="-9" r="3" /><text x="-58" y="-5" className="memory-map-node-kind">{node.archivedAt ? 'Archived' : node.supersededBy ? 'Previous version' : node.lifecycle?.status === 'stale' ? 'Needs review' : kindLabel(node.kind)}</text><text x="-68" y="14" className="memory-map-node-title">{shortLabel(node.title || node.id)}</text></>}</g>;
        })}
      </g>
    </svg>
    <div className="memory-map-hint"><div className="memory-map-link-legend"><span><i />Evidence</span><span><i className="inferred" />Semantic similarity</span><span><i className="conflict" />Contradiction</span></div>Stronger connections draw memories closer. Line thickness shows strength; hover a link for its scores. Drag nodes to arrange them; reset to restore weighted proximity.{!edges.length && ' No connections match this view.'}{graph?.truncated && ' Refine your search to explore another neighborhood.'}</div>
  </div>;
}

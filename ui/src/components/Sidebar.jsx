import logo from '../assets/logo-admin.svg';
import { NAV, RAIL, RAIL_BOTTOM } from '../nav';
import { VIEW_RIGHTS } from '../rights';

/** Rail ikon global — pindah cepat antar fungsi besar. */
export function IconRail({ view, onNavigate }) {
  return (
    <div className="rail">
      <img className="rail-logo" src={logo} alt="Retriva" />
      {RAIL.map(({ id, Icon, title }) => (
        <button key={id} title={title}
          className={`rail-btn${view === id ? ' on' : ''}`}
          onClick={() => onNavigate(id)}>
          <Icon size={19} />
        </button>
      ))}
      <div className="rail-sp" />
      {RAIL_BOTTOM.map(({ id, Icon, title }) => (
        <button key={id} title={title}
          className={`rail-btn${view === id ? ' on' : ''}`}
          onClick={() => onNavigate(id)}>
          <Icon size={19} />
        </button>
      ))}
    </div>
  );
}

/** Panel navigasi rinci. */
export function NavPanel({ view, onNavigate, unitCount, online, panelRole, badges, labName, can, appVersion }) {
  // Menu yang seluruh isinya akan ditolak tidak ditampilkan. Yang menegakkan
  // tetap Superadmin; ini hanya menghindari pintu yang terkunci tanpa tanda.
  const allow = can ?? (() => true);
  const sections = NAV
    .map((g) => ({
      ...g,
      items: g.items.filter((i) => !VIEW_RIGHTS[i.id] || allow(VIEW_RIGHTS[i.id])),
    }))
    .filter((g) => g.items.length > 0);

  return (
    <nav className="nav">
      <div className="nav-head">
        <div className="nav-title">{labName ?? 'Laboratorium Information Retrieval'}</div>
        <div className="nav-sub">{panelRole ?? 'Belum tertaut'} · {online} unit aktif</div>
      </div>

      {sections.map((g) => (
        <div className="nav-sec" key={g.group}>
          <div className="nav-sec-label">{g.group}</div>
          {g.items.map(({ id, Icon, label }) => (
            <button key={id}
              className={`nav-item${view === id ? ' on' : ''}`}
              onClick={() => onNavigate(id)}>
              <Icon size={17} />
              <span>{label}</span>
              {id === 'dashboard' && <span className="nav-count">{unitCount}</span>}
              {badges?.[id] ? <span className="nav-count alert">{badges[id]}</span> : null}
            </button>
          ))}
        </div>
      ))}

      <div className="nav-foot">
        <span className={`badge-dot${online ? '' : ' off'}`} />
        Retriva v{appVersion ?? '1.0.0'} · port 8443
      </div>
    </nav>
  );
}

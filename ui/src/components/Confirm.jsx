import { Warning } from '@phosphor-icons/react';

/**
 * Konfirmasi untuk perintah yang tidak bisa ditarik dengan mudah.
 * Mematikan 40 PC di tengah praktikum adalah kesalahan yang mahal,
 * dan satu klik saja terlalu murah untuk tindakan sebesar itu.
 */
export default function Confirm({ open, title, body, confirmLabel, onConfirm, onCancel }) {
  if (!open) return null;
  return (
    <div className="modal-veil" onClick={onCancel}>
      <div className="modal" onClick={(e) => e.stopPropagation()}>
        <div className="modal-head">
          <Warning size={18} />
          <span>{title}</span>
        </div>
        <div className="modal-body">{body}</div>
        <div className="modal-foot">
          <button className="btn" onClick={onCancel}>Batal</button>
          <button className="btn btn-primary" onClick={onConfirm}>{confirmLabel}</button>
        </div>
      </div>
    </div>
  );
}

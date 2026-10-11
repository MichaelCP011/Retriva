// Font dibundel lewat npm, bukan diambil dari CDN. PC laboratorium
// belum tentu punya akses internet, dan WebView2 yang gagal memuat font
// eksternal akan merusak seluruh tata letak.
import '@fontsource/source-sans-3/400.css';
import '@fontsource/source-sans-3/500.css';
import '@fontsource/source-sans-3/600.css';
import '@fontsource/source-sans-3/700.css';
import '@fontsource/source-code-pro/400.css';
import '@fontsource/source-code-pro/500.css';

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import ClientApp from './ClientApp.jsx';
import '../styles.css';
import './client.css';

createRoot(document.getElementById('root')).render(
  <StrictMode>
    <ClientApp />
  </StrictMode>
);

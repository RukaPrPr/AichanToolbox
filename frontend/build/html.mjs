// Rollup and Vite share one light-first HTML shell. Do not duplicate its theme here.
export function buildProductionHtml(template) {
  const entry = /<script\s+type="module"\s+src="\/src\/main\.ts"><\/script>/g
  if ([...template.matchAll(entry)].length !== 1 || !template.includes('</head>')) {
    throw new Error('Expected one frontend entry and a closing head in index.html')
  }
  return template
    .replace(entry, '<script type="module" src="./assets/app.js"></script>')
    .replace('</head>', '  <link rel="stylesheet" href="./styles.css" />\n  </head>')
}

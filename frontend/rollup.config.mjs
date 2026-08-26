import fs from 'node:fs'
import path from 'node:path'
import ts from 'typescript'
import vue from 'rollup-plugin-vue'
import { nodeResolve } from '@rollup/plugin-node-resolve'
import terser from '@rollup/plugin-terser'

const outputRoot = path.resolve('../desktop/wwwroot')

function typescriptPlugin() {
  return {
    name: 'typescript-transpile',
    transform(code, id) {
      if (!id.endsWith('.ts') && !id.includes('lang.ts')) return null
      const result = ts.transpileModule(code, {
        compilerOptions: {
          target: ts.ScriptTarget.ES2022,
          module: ts.ModuleKind.ESNext,
          sourceMap: false,
          useDefineForClassFields: true
        },
        fileName: id
      })
      return { code: result.outputText, map: null }
    }
  }
}

function compileConstants() {
  const values = {
    'process.env.NODE_ENV': JSON.stringify('production'),
    '__VUE_OPTIONS_API__': 'false',
    '__VUE_PROD_DEVTOOLS__': 'false',
    '__VUE_PROD_HYDRATION_MISMATCH_DETAILS__': 'false'
  }
  return {
    name: 'compile-constants',
    transform(code) {
      let result = code
      for (const [from, to] of Object.entries(values)) result = result.split(from).join(to)
      return result === code ? null : { code: result, map: null }
    }
  }
}

function staticAssets() {
  const stylePath = path.resolve('src/styles.css')
  const iconPath = path.resolve('../desktop/Assets/aichan-windows.ico')
  const programLogoPath = path.resolve('../desktop/Assets/aichan-startup.png')
  const dragCursorPath = path.resolve('src/cursors/aichan-drag-cursor.png')
  return {
    name: 'static-assets',
    buildStart() {
      const desktopRoot = path.resolve('../desktop')
      const relativeOutput = path.relative(desktopRoot, outputRoot)
      if (relativeOutput.startsWith('..') || path.isAbsolute(relativeOutput)) {
        throw new Error(`Refusing to clean frontend output outside desktop: ${outputRoot}`)
      }
      fs.rmSync(outputRoot, { recursive: true, force: true })
    },
    load(id) {
      if (path.resolve(id) === stylePath) return 'export default undefined'
      return null
    },
    generateBundle() {
      this.emitFile({ type: 'asset', fileName: 'styles.css', source: fs.readFileSync(stylePath, 'utf8') })
      this.emitFile({ type: 'asset', fileName: 'aichan.ico', source: fs.readFileSync(iconPath) })
      this.emitFile({ type: 'asset', fileName: 'aichan-program.png', source: fs.readFileSync(programLogoPath) })
      this.emitFile({ type: 'asset', fileName: 'cursors/aichan-drag-cursor.png', source: fs.readFileSync(dragCursorPath) })
      this.emitFile({
        type: 'asset',
        fileName: 'index.html',
        source: '<!doctype html><html lang="zh-CN"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="dark"><title>艾酱图片工具箱</title><script>window.__AICHAN_BOOT__={htmlStartedAt:performance.now()}</script><link rel="stylesheet" href="./styles.css"></head><body><div id="app"></div><script type="module" src="./assets/app.js"></script></body></html>'
      })
    }
  }
}

export default {
  input: 'src/main.ts',
  plugins: [
    vue({ target: 'browser' }),
    typescriptPlugin(),
    compileConstants(),
    nodeResolve({ browser: true, extensions: ['.mjs', '.js', '.json', '.ts', '.vue'] }),
    staticAssets(),
    terser({
      compress: { passes: 2 },
      format: { comments: false },
      module: true
    })
  ],
  output: {
    dir: '../desktop/wwwroot',
    entryFileNames: 'assets/app.js',
    format: 'es',
    sourcemap: false
  },
  onwarn(warning, warn) {
    if (warning.code !== 'MODULE_LEVEL_DIRECTIVE') warn(warning)
  }
}

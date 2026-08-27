import fs from 'node:fs'
import path from 'node:path'
import ts from 'typescript'
import vue from 'rollup-plugin-vue'
import { nodeResolve } from '@rollup/plugin-node-resolve'
import terser from '@rollup/plugin-terser'
import { buildProductionHtml } from './build/html.mjs'

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
  const publicRoot = path.resolve('public')
  const stylePaths = ['src/styles.css', 'src/themes.css'].map(file => path.resolve(file))
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
      if (stylePaths.includes(path.resolve(id))) return 'export default undefined'
      return null
    },
    generateBundle() {
      // Match Vite's public/ convention, including future theme assets.
      const emitPublic = (directory, prefix = '') => {
        if (!fs.existsSync(directory)) return
        for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
          if (entry.isSymbolicLink()) throw new Error(`Public assets must not be symlinks: ${entry.name}`)
          const file = path.join(directory, entry.name)
          const fileName = `${prefix}${entry.name}`
          if (entry.isDirectory()) emitPublic(file, `${fileName}/`)
          else if (entry.isFile()) this.emitFile({ type: 'asset', fileName, source: fs.readFileSync(file) })
        }
      }
      emitPublic(publicRoot)
      this.emitFile({ type: 'asset', fileName: 'styles.css', source: stylePaths.map(file => fs.readFileSync(file, 'utf8')).join('\n') })
      this.emitFile({ type: 'asset', fileName: 'aichan.ico', source: fs.readFileSync(iconPath) })
      this.emitFile({ type: 'asset', fileName: 'aichan-program.png', source: fs.readFileSync(programLogoPath) })
      this.emitFile({ type: 'asset', fileName: 'cursors/aichan-drag-cursor.png', source: fs.readFileSync(dragCursorPath) })
      this.emitFile({
        type: 'asset',
        fileName: 'index.html',
        source: buildProductionHtml(fs.readFileSync(path.resolve('index.html'), 'utf8'))
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

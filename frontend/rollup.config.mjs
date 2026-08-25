import fs from 'node:fs'
import path from 'node:path'
import ts from 'typescript'
import vue from 'rollup-plugin-vue'
import { nodeResolve } from '@rollup/plugin-node-resolve'

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
    '__VUE_OPTIONS_API__': 'true',
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
  const grabCursorPath = path.resolve('src/cursors/canvas-grab.svg')
  const grabbingCursorPath = path.resolve('src/cursors/canvas-grabbing.svg')
  return {
    name: 'static-assets',
    load(id) {
      if (path.resolve(id) === stylePath) return 'export default undefined'
      return null
    },
    generateBundle() {
      this.emitFile({ type: 'asset', fileName: 'styles.css', source: fs.readFileSync(stylePath, 'utf8') })
      this.emitFile({ type: 'asset', fileName: 'aichan.ico', source: fs.readFileSync(iconPath) })
      this.emitFile({ type: 'asset', fileName: 'aichan-program.png', source: fs.readFileSync(programLogoPath) })
      this.emitFile({ type: 'asset', fileName: 'cursors/canvas-grab.svg', source: fs.readFileSync(grabCursorPath) })
      this.emitFile({ type: 'asset', fileName: 'cursors/canvas-grabbing.svg', source: fs.readFileSync(grabbingCursorPath) })
      this.emitFile({
        type: 'asset',
        fileName: 'index.html',
        source: '<!doctype html><html lang="zh-CN"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="dark"><title>艾酱图片工具箱</title><link rel="stylesheet" href="./styles.css"></head><body><div id="app"></div><script type="module" src="./assets/app.js"></script></body></html>'
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
    staticAssets()
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

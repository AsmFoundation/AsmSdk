// AsmSdk site: options for DocFX's modern template (docs/template/public/main.js).
//
// The modern template imports this module and reads the exported object:
//   - defaultTheme 'auto' follows the reader's OS setting; the toggle still switches it.
//   - iconLinks adds the GitHub icon to the top bar.
//   - configureHljs teaches highlight.js the names this site uses for NASM code fences.
export default {
  defaultTheme: 'auto',
  iconLinks: [
    {
      icon: 'github',
      href: 'https://github.com/AsmFoundation/AsmSdk',
      title: 'AsmSdk on GitHub'
    }
  ],

  /**
   * Registers ```nasm and ```asm as aliases of highlight.js's x86asm grammar, so NASM
   * samples are highlighted wherever they are written with the natural fence name.
   * @param {object} hljs The highlight.js instance the template uses.
   */
  configureHljs: (hljs) => {
    // Guard: a highlight.js build without x86asm would throw on the alias; keep pages working.
    if (hljs.getLanguage('x86asm')) {
      hljs.registerAliases(['nasm', 'asm'], { languageName: 'x86asm' })
    }
  }
}

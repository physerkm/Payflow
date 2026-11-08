import { createTheme } from '@mui/material/styles';

// Chart palette (validated: categorical slots 1-3, light & dark steps; see docs/architecture/frontend.md).
export const vizPalette = {
  light: {
    series: ['#2a78d6', '#eb6834', '#1baf7a'],
    status: { good: '#0ca30c', warning: '#fab219', serious: '#ec835a', critical: '#d03b3b' },
    grid: '#e1e0d9',
    axis: '#c3c2b7',
    muted: '#898781',
  },
  dark: {
    series: ['#3987e5', '#d95926', '#199e70'],
    status: { good: '#0ca30c', warning: '#fab219', serious: '#ec835a', critical: '#d03b3b' },
    grid: '#2c2c2a',
    axis: '#383835',
    muted: '#898781',
  },
};

export function createAppTheme(mode) {
  const dark = mode === 'dark';
  return createTheme({
    palette: {
      mode,
      primary: { main: dark ? '#3987e5' : '#2a78d6' },
      secondary: { main: dark ? '#9085e9' : '#6250d6' },
      success: { main: dark ? '#0ca30c' : '#00800a' },
      error: { main: '#d03b3b' },
      warning: { main: dark ? '#fab219' : '#b86e00' },
      background: dark ? { default: '#0d0d0d', paper: '#1a1a19' } : { default: '#f6f6f3', paper: '#ffffff' },
      text: dark ? { primary: '#f0efec', secondary: '#c3c2b7' } : { primary: '#0b0b0b', secondary: '#52514e' },
      divider: dark ? 'rgba(255,255,255,0.10)' : 'rgba(11,11,11,0.10)',
    },
    shape: { borderRadius: 10 },
    typography: {
      fontFamily: '"Inter", system-ui, -apple-system, "Segoe UI", sans-serif',
      h4: { fontWeight: 700, letterSpacing: '-0.02em' },
      h5: { fontWeight: 700, letterSpacing: '-0.01em' },
      h6: { fontWeight: 600 },
      button: { textTransform: 'none', fontWeight: 600 },
    },
    components: {
      MuiPaper: { defaultProps: { elevation: 0 }, styleOverrides: { root: { backgroundImage: 'none' } } },
      MuiCard: {
        defaultProps: { variant: 'outlined' },
      },
      MuiButton: { defaultProps: { disableElevation: true } },
      MuiTextField: { defaultProps: { size: 'small', fullWidth: true } },
      MuiChip: { styleOverrides: { root: { fontWeight: 600 } } },
      MuiTableCell: { styleOverrides: { root: { fontVariantNumeric: 'tabular-nums' } } },
    },
  });
}

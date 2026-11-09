import { useState } from 'react';
import { NavLink, Outlet, useNavigate } from 'react-router';
import { useQuery } from '@tanstack/react-query';
import {
  AppBar, Avatar, Badge, Box, Divider, Drawer, IconButton, List, ListItem, ListItemButton, ListItemIcon, ListItemText, ListSubheader,
  Menu, MenuItem, Popover, Stack, Toolbar, Tooltip, Typography, useMediaQuery, useTheme,
} from '@mui/material';
import MenuIcon from '@mui/icons-material/Menu';
import DashboardOutlinedIcon from '@mui/icons-material/DashboardOutlined';
import PeopleOutlineIcon from '@mui/icons-material/PeopleOutline';
import Inventory2OutlinedIcon from '@mui/icons-material/Inventory2Outlined';
import ReceiptLongOutlinedIcon from '@mui/icons-material/ReceiptLongOutlined';
import PaymentsOutlinedIcon from '@mui/icons-material/PaymentsOutlined';
import UndoOutlinedIcon from '@mui/icons-material/UndoOutlined';
import GroupsOutlinedIcon from '@mui/icons-material/GroupsOutlined';
import InsightsOutlinedIcon from '@mui/icons-material/InsightsOutlined';
import SettingsOutlinedIcon from '@mui/icons-material/SettingsOutlined';
import NotificationsNoneIcon from '@mui/icons-material/NotificationsNone';
import DarkModeOutlinedIcon from '@mui/icons-material/DarkModeOutlined';
import LightModeOutlinedIcon from '@mui/icons-material/LightModeOutlined';
import UnfoldMoreIcon from '@mui/icons-material/UnfoldMore';
import CheckIcon from '@mui/icons-material/Check';
import AddIcon from '@mui/icons-material/Add';
import AutoAwesomeOutlinedIcon from '@mui/icons-material/AutoAwesomeOutlined';
import AdminPanelSettingsOutlinedIcon from '@mui/icons-material/AdminPanelSettingsOutlined';
import { useAuth, useTenantKey } from '../auth/AuthProvider';
import { authApi, miscApi } from '../api/endpoints';
import { useColorMode } from '../colorMode';
import { formatDateTime, humanize } from '../lib/format';

const DRAWER_WIDTH = 248;

// Text for screen readers only.
const visuallyHidden = { position: 'absolute', width: '1px', height: '1px', p: 0, m: '-1px', overflow: 'hidden', clip: 'rect(0 0 0 0)', whiteSpace: 'nowrap', border: 0 };

const NAV = [
  { section: 'Overview', items: [{ to: '/', label: 'Dashboard', icon: DashboardOutlinedIcon, end: true }] },
  {
    section: 'Operations',
    items: [
      { to: '/customers', label: 'Customers', icon: PeopleOutlineIcon, permission: 'customers.read' },
      { to: '/products', label: 'Products', icon: Inventory2OutlinedIcon, permission: 'products.read' },
      { to: '/orders', label: 'Orders', icon: ReceiptLongOutlinedIcon, permission: 'orders.read' },
    ],
  },
  {
    section: 'Finance',
    items: [
      { to: '/payments', label: 'Payments', icon: PaymentsOutlinedIcon, permission: 'payments.read' },
      { to: '/refunds', label: 'Refunds', icon: UndoOutlinedIcon, permission: 'payments.read' },
      { to: '/reports', label: 'Reports', icon: InsightsOutlinedIcon, permission: 'reports.read' },
      { to: '/assistant', label: 'AI assistant', icon: AutoAwesomeOutlinedIcon, permission: 'reports.read' },
    ],
  },
  {
    section: 'Administration',
    items: [
      { to: '/team', label: 'Team', icon: GroupsOutlinedIcon, permission: 'members.manage' },
      { to: '/settings', label: 'Settings', icon: SettingsOutlinedIcon, permission: 'tenant.manage' },
    ],
  },
];

function Navigation({ onNavigate }) {
  const { can } = useAuth();
  const me = useQuery({ queryKey: ['me'], queryFn: authApi.me, staleTime: 60_000 });
  return (
    <Box component="nav" aria-label="Main navigation" sx={{ py: 1 }}>
      {NAV.map((group) => {
        // Hiding items is a UX convenience only; every endpoint enforces the same permission on the server.
        const items = group.items.filter((item) => !item.permission || can(item.permission));
        if (items.length === 0) return null;
        return (
          <List key={group.section} dense subheader={<ListSubheader sx={{ bgcolor: 'transparent', lineHeight: '32px' }}>{group.section}</ListSubheader>}>
            {items.map(({ to, label, icon: Icon, end }) => (
              <ListItem key={to} disablePadding>
              <ListItemButton
                component={NavLink}
                to={to}
                end={end}
                onClick={onNavigate}
                sx={{
                  mx: 1, borderRadius: 2,
                  '&.active': { bgcolor: 'action.selected', '& .MuiListItemIcon-root': { color: 'primary.main' }, '& .MuiListItemText-primary': { fontWeight: 600 } },
                }}
              >
                <ListItemIcon sx={{ minWidth: 36 }}><Icon fontSize="small" /></ListItemIcon>
                <ListItemText primary={label} />
              </ListItemButton>
              </ListItem>
            ))}
          </List>
        );
      })}
      {me.data?.platformRole && (
        <List dense subheader={<ListSubheader sx={{ bgcolor: 'transparent', lineHeight: '32px' }}>Platform</ListSubheader>}>
          <ListItem disablePadding>
            <ListItemButton component={NavLink} to="/platform" onClick={onNavigate} sx={{ mx: 1, borderRadius: 2 }}>
              <ListItemIcon sx={{ minWidth: 36 }}><AdminPanelSettingsOutlinedIcon fontSize="small" /></ListItemIcon>
              <ListItemText primary="Platform admin" />
            </ListItemButton>
          </ListItem>
        </List>
      )}
    </Box>
  );
}

function TenantSwitcher() {
  const { tenant, switchTenant } = useAuth();
  const navigate = useNavigate();
  const [anchor, setAnchor] = useState(null);
  const memberships = useQuery({ queryKey: ['memberships'], queryFn: authApi.memberships, enabled: Boolean(anchor) });

  const choose = async (tenantId) => {
    setAnchor(null);
    if (tenantId !== tenant.tenantId) {
      await switchTenant(tenantId);
      navigate('/');
    }
  };

  return (
    <>
      {/* The visible company name is part of the accessible name (WCAG 2.5.3); the prefix is for screen readers. */}
      <ListItemButton onClick={(e) => setAnchor(e.currentTarget)} aria-haspopup="menu" sx={{ mx: 1, my: 1, flexGrow: 0, borderRadius: 2, border: 1, borderColor: 'divider' }}>
        <Box component="span" sx={visuallyHidden}>Switch tenant: </Box>
        <Avatar aria-hidden variant="rounded" sx={{ width: 32, height: 32, mr: 1.5, bgcolor: 'primary.main', fontSize: 14, fontWeight: 700 }}>
          {tenant.name.slice(0, 2).toUpperCase()}
        </Avatar>
        <ListItemText primary={tenant.name} secondary={humanize(tenant.role)} primaryTypographyProps={{ noWrap: true, fontWeight: 600 }} />
        <UnfoldMoreIcon fontSize="small" color="action" />
      </ListItemButton>
      <Menu anchorEl={anchor} open={Boolean(anchor)} onClose={() => setAnchor(null)} slotProps={{ paper: { sx: { minWidth: 240 } } }}>
        {memberships.data?.filter((m) => m.status === 'Active' && m.tenantStatus === 'Active').map((m) => (
          <MenuItem key={m.tenantId} onClick={() => choose(m.tenantId)} selected={m.tenantId === tenant.tenantId}>
            <ListItemIcon>{m.tenantId === tenant.tenantId && <CheckIcon fontSize="small" />}</ListItemIcon>
            <ListItemText primary={m.tenantName} secondary={humanize(m.role)} />
          </MenuItem>
        ))}
        <Divider />
        <MenuItem onClick={() => { setAnchor(null); navigate('/tenants'); }}>
          <ListItemIcon><AddIcon fontSize="small" /></ListItemIcon>
          <ListItemText primary="Create or join a tenant" />
        </MenuItem>
      </Menu>
    </>
  );
}

function NotificationsButton() {
  const { tenant } = useAuth();
  const [anchor, setAnchor] = useState(null);
  const key = useTenantKey('notifications');
  const notifications = useQuery({ queryKey: key, queryFn: () => miscApi.notifications(15), refetchInterval: 30_000 });
  const recent = notifications.data ?? [];
  const lastSeenKey = `payflow.notificationsSeen.${tenant.tenantId}`;
  const [lastSeen, setLastSeen] = useState(() => localStorage.getItem(lastSeenKey) ?? '');
  const unread = recent.filter((n) => n.createdAt > lastSeen).length;

  const open = (event) => {
    setAnchor(event.currentTarget);
    if (recent[0]) {
      localStorage.setItem(lastSeenKey, recent[0].createdAt);
      setLastSeen(recent[0].createdAt);
    }
  };

  return (
    <>
      <Tooltip title="Notifications">
        {/* The name starts with the visible badge count (WCAG 2.5.3 label in name). */}
        <IconButton onClick={open} aria-label={unread > 0 ? `${unread} unread notifications` : 'Notifications'}>
          <Badge badgeContent={unread} color="error"><NotificationsNoneIcon /></Badge>
        </IconButton>
      </Tooltip>
      <Popover open={Boolean(anchor)} anchorEl={anchor} onClose={() => setAnchor(null)} anchorOrigin={{ vertical: 'bottom', horizontal: 'right' }} transformOrigin={{ vertical: 'top', horizontal: 'right' }}>
        <Box sx={{ width: 360, maxHeight: 420, overflow: 'auto' }}>
          <Typography variant="subtitle2" sx={{ px: 2, pt: 2, pb: 1 }}>Recent activity</Typography>
          {recent.length === 0 && <Typography variant="body2" color="text.secondary" sx={{ px: 2, pb: 2 }}>No notifications yet.</Typography>}
          <List dense>
            {recent.map((n) => (
              <ListItem key={n.id} disablePadding>
                <ListItemButton component={NavLink} to={n.resourceType === 'payment' ? `/payments/${n.resourceId}` : '/'} onClick={() => setAnchor(null)}>
                  <ListItemText primary={n.title} secondary={<>{n.message}<br />{formatDateTime(n.createdAt, tenant.timeZone)}</>} />
                </ListItemButton>
              </ListItem>
            ))}
          </List>
        </Box>
      </Popover>
    </>
  );
}

function UserMenu() {
  const { user, logout } = useAuth();
  const [anchor, setAnchor] = useState(null);
  return (
    <>
      <IconButton onClick={(e) => setAnchor(e.currentTarget)} aria-label="Account menu">
        <Avatar sx={{ width: 32, height: 32, fontSize: 14 }}>{user.displayName.slice(0, 1).toUpperCase()}</Avatar>
      </IconButton>
      <Menu anchorEl={anchor} open={Boolean(anchor)} onClose={() => setAnchor(null)}>
        <Box sx={{ px: 2, py: 1 }}>
          <Typography variant="subtitle2">{user.displayName}</Typography>
          <Typography variant="caption" color="text.secondary">{user.email}</Typography>
        </Box>
        <Divider />
        <MenuItem onClick={() => { setAnchor(null); logout(); }}>Sign out</MenuItem>
      </Menu>
    </>
  );
}

export default function AppLayout() {
  const theme = useTheme();
  const desktop = useMediaQuery(theme.breakpoints.up('md'));
  const [mobileOpen, setMobileOpen] = useState(false);
  const colorMode = useColorMode();

  const drawer = (
    <Stack sx={{ height: '100%' }}>
      <Toolbar sx={{ gap: 1 }}>
        <Box component="img" src="/favicon.svg" alt="" sx={{ width: 28, height: 28 }} />
        <Typography variant="h6" fontWeight={800} letterSpacing="-0.02em">PayFlow</Typography>
      </Toolbar>
      <TenantSwitcher />
      <Box sx={{ flex: 1, overflowY: 'auto' }}><Navigation onNavigate={() => setMobileOpen(false)} /></Box>
      <Typography variant="caption" color="text.secondary" sx={{ p: 2 }}>Simulated provider · no real money moves</Typography>
    </Stack>
  );

  return (
    <Box sx={{ display: 'flex', minHeight: '100vh' }}>
      <AppBar position="fixed" color="inherit" elevation={0} sx={{ borderBottom: 1, borderColor: 'divider', width: { md: `calc(100% - ${DRAWER_WIDTH}px)` }, ml: { md: `${DRAWER_WIDTH}px` } }}>
        <Toolbar>
          {!desktop && (
            <IconButton edge="start" onClick={() => setMobileOpen(true)} aria-label="Open navigation" sx={{ mr: 1 }}><MenuIcon /></IconButton>
          )}
          <Box sx={{ flex: 1 }} />
          <Tooltip title={colorMode.mode === 'dark' ? 'Light mode' : 'Dark mode'}>
            <IconButton onClick={colorMode.toggle} aria-label="Toggle color mode">
              {colorMode.mode === 'dark' ? <LightModeOutlinedIcon /> : <DarkModeOutlinedIcon />}
            </IconButton>
          </Tooltip>
          <NotificationsButton />
          <UserMenu />
        </Toolbar>
      </AppBar>
      <Box component="aside" sx={{ width: { md: DRAWER_WIDTH }, flexShrink: { md: 0 } }}>
        <Drawer
          variant={desktop ? 'permanent' : 'temporary'}
          open={desktop || mobileOpen}
          onClose={() => setMobileOpen(false)}
          ModalProps={{ keepMounted: true }}
          sx={{ '& .MuiDrawer-paper': { width: DRAWER_WIDTH, boxSizing: 'border-box', borderRight: 1, borderColor: 'divider' } }}
        >
          {drawer}
        </Drawer>
      </Box>
      <Box component="main" sx={{ flex: 1, minWidth: 0, p: { xs: 2, md: 4 }, mt: 8 }}>
        <Outlet />
      </Box>
    </Box>
  );
}

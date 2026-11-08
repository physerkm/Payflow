import { Link as RouterLink } from 'react-router';
import { Box, Button, Stack, Typography } from '@mui/material';

export default function NotFoundPage() {
  return (
    <Box sx={{ minHeight: '100vh', display: 'grid', placeItems: 'center', p: 2 }}>
      <Stack spacing={2} alignItems="center" textAlign="center">
        <Typography variant="h3" component="h1" fontWeight={800}>404</Typography>
        <Typography color="text.secondary">This page does not exist, or it belongs to a tenant you cannot access.</Typography>
        <Button component={RouterLink} to="/" variant="contained">Back to dashboard</Button>
      </Stack>
    </Box>
  );
}

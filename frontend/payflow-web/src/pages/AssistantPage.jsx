import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { Alert, Box, Button, Card, CardContent, Chip, Stack, TextField, Typography } from '@mui/material';
import AutoAwesomeOutlinedIcon from '@mui/icons-material/AutoAwesomeOutlined';
import { useTenantKey } from '../auth/AuthProvider';
import { assistantApi } from '../api/endpoints';
import { errorMessage } from '../api/client';
import { PageHeader } from '../components/Common';
import { LoadingState } from '../components/DataState';
import { formatNumber } from '../lib/format';

const EXAMPLES = [
  'How did the payment success rate change this month?',
  'Summarize last week’s failed payments by reason.',
  'How much did we refund in the last 30 days?',
  'Compare revenue of yesterday and the day before.',
];

function Answer({ entry }) {
  return (
    <Card>
      <CardContent>
        <Typography variant="subtitle2" color="text.secondary" gutterBottom>{entry.question}</Typography>
        {entry.error ? (
          <Alert severity="error">{errorMessage(entry.error)}</Alert>
        ) : (
          <>
            {entry.answer.refused && <Alert severity="warning" sx={{ mb: 1 }}>The assistant declined this request.</Alert>}
            <Typography sx={{ whiteSpace: 'pre-wrap' }}>{entry.answer.answer}</Typography>
            <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap sx={{ mt: 2 }}>
              {entry.answer.toolsUsed.map((t, i) => (
                <Chip key={i} size="small" variant="outlined" color={t.succeeded ? 'default' : 'error'} label={`${t.tool} ${t.input}`} title="Report tool called on your behalf" />
              ))}
            </Stack>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
              {entry.answer.model} · {formatNumber(entry.answer.inputTokens + entry.answer.outputTokens)} tokens · {(entry.answer.latencyMs / 1000).toFixed(1)} s
            </Typography>
          </>
        )}
      </CardContent>
    </Card>
  );
}

export default function AssistantPage() {
  const [question, setQuestion] = useState('');
  const [history, setHistory] = useState([]);
  const status = useQuery({ queryKey: useTenantKey('assistant-status'), queryFn: assistantApi.status });
  const ask = useMutation({
    mutationFn: (q) => assistantApi.ask(q),
    onSuccess: (answer, q) => setHistory((h) => [{ question: q, answer }, ...h]),
    onError: (error, q) => setHistory((h) => [{ question: q, error }, ...h]),
  });

  const submit = (q) => {
    const text = (q ?? question).trim();
    if (!text || ask.isPending) return;
    setQuestion('');
    ask.mutate(text);
  };

  if (status.isPending) return <LoadingState />;
  const enabled = status.data?.enabled;

  return (
    <>
      <PageHeader
        title="AI assistant"
        subtitle="Ask about payments, refunds and revenue. Read-only: it can only call a few report tools for your company and cannot change anything."
      />
      {!enabled && (
        <Alert severity="info" sx={{ mb: 3 }}>
          The assistant is not configured on this server. Set <code>Assistant__ApiKey</code> (an Anthropic API key) for the API to enable it.
        </Alert>
      )}
      <Card sx={{ mb: 3 }}>
        <CardContent>
          <Stack component="form" direction={{ xs: 'column', sm: 'row' }} spacing={1.5} onSubmit={(e) => { e.preventDefault(); submit(); }}>
            <TextField
              label="Your question"
              value={question}
              onChange={(e) => setQuestion(e.target.value)}
              disabled={!enabled}
              slotProps={{ htmlInput: { maxLength: 1000 } }}
            />
            <Button type="submit" variant="contained" startIcon={<AutoAwesomeOutlinedIcon />} disabled={!enabled || ask.isPending || !question.trim()} sx={{ whiteSpace: 'nowrap' }}>
              {ask.isPending ? 'Thinking…' : 'Ask'}
            </Button>
          </Stack>
          <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap sx={{ mt: 2 }}>
            {EXAMPLES.map((e) => <Chip key={e} label={e} onClick={() => submit(e)} disabled={!enabled || ask.isPending} variant="outlined" />)}
          </Stack>
        </CardContent>
      </Card>
      <Stack spacing={2}>
        {ask.isPending && <LoadingState rows={1} />}
        {history.map((entry, i) => <Answer key={i} entry={entry} />)}
        {history.length === 0 && enabled && (
          <Box sx={{ textAlign: 'center', py: 4 }}>
            <Typography variant="body2" color="text.secondary">Answers always name the metric and the date range they are based on.</Typography>
          </Box>
        )}
      </Stack>
    </>
  );
}
